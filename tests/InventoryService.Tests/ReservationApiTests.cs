using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryService.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Inventory;
using Xunit;

namespace InventoryService.Tests;

[Trait("Category", "Integration")]
[Collection("Inventory SQL")]
public sealed class ReservationApiTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    [Fact]
    public async Task MultipleItemsReserveTogetherAndGetDoesNotChangeStock()
    {
        var first = await StockAsync(10);
        var second = await StockAsync(8);
        var request = Request(Guid.NewGuid(), (first, 3), (second, 2));
        using var created = await PostAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = (await created.Content.ReadFromJsonAsync<ReservationResponse>())!;
        Assert.Equal(request.ReservationId, body.ReservationId);
        Assert.Equal("Reserved", body.Status);
        Assert.Null(body.FailureCode);
        Assert.Empty(body.Shortfalls);
        Assert.EndsWith($"/api/inventory/reservations/{body.ReservationId}", created.Headers.Location!.AbsolutePath);

        for (var read = 0; read < 2; read++)
        {
            var queried = await fixture.Client.GetFromJsonAsync<ReservationResponse>(created.Headers.Location);
            Assert.Equal(body.ReservationId, queried!.ReservationId);
            Assert.Equal("Reserved", queried.Status);
        }

        InventoryAssertions.EqualBalance(new InventoryResponse(first, 10, 3, 7), await BalanceAsync(first));
        InventoryAssertions.EqualBalance(new InventoryResponse(second, 8, 2, 6), await BalanceAsync(second));
        await using var database = fixture.CreateContext();
        var movements = await database.StockMovements.Where(item => item.OperationId == body.ReservationId).ToArrayAsync();
        Assert.Equal(2, movements.Length);
        Assert.All(movements, movement => Assert.Equal("Reserve", movement.MovementType));
        Assert.All(movements, movement => Assert.Equal(0, movement.OnHandDelta));
        Assert.Equal(2, await database.ReservationLines.CountAsync(item => item.ReservationId == body.ReservationId));
        Assert.Equal(1, fixture.Products.CallCount(first));
        Assert.Equal(1, fixture.Products.CallCount(second));
    }

    [Fact]
    public async Task T03_OneInsufficientItemRejectsAllLinesWithoutStockOrLedgerChanges()
    {
        var enough = await StockAsync(10);
        var scarce = await StockAsync(1);
        var id = Guid.NewGuid();
        using var response = await PostAsync(Request(id, (enough, 3), (scarce, 2)));
        await AssertProblemAsync(response, 409, "INSUFFICIENT_STOCK");
        Assert.Equal(new[] { new StockShortfall(scarce, 2, 1) }, await ShortfallsAsync(response));
        InventoryAssertions.EqualBalance(new InventoryResponse(enough, 10, 0, 10), await BalanceAsync(enough));
        InventoryAssertions.EqualBalance(new InventoryResponse(scarce, 1, 0, 1), await BalanceAsync(scarce));
        Assert.Single(await fixture.GetMovementsAsync(enough));
        Assert.Single(await fixture.GetMovementsAsync(scarce));
        await using var database = fixture.CreateContext();
        var reservation = await database.Reservations.SingleAsync(item => item.Id == id);
        Assert.Equal("Rejected", reservation.Status);
        Assert.Equal("INSUFFICIENT_STOCK", reservation.FailureCode);
        Assert.NotNull(reservation.FailureDetailsJson);
        Assert.Equal(2, await database.ReservationLines.CountAsync(item => item.ReservationId == id));
        Assert.False(await database.StockMovements.AnyAsync(item => item.OperationId == id));
    }

    [Fact]
    public async Task T04_TwentyConcurrentReservationsForTenUnitsNeverOversell()
    {
        var product = await StockAsync(10);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = ids.Select(async id =>
        {
            await start.Task;
            return await PostAsync(Request(id, (product, 1)));
        }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(tasks);
        try
        {
            Assert.Equal(10, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(10, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            foreach (var rejected in responses.Where(response => response.StatusCode == HttpStatusCode.Conflict))
            {
                await AssertProblemAsync(rejected, 409, "INSUFFICIENT_STOCK");
            }

            InventoryAssertions.EqualBalance(new InventoryResponse(product, 10, 10, 0), await BalanceAsync(product));
            await using var database = fixture.CreateContext();
            var headers = await database.Reservations.Where(item => ids.Contains(item.Id)).ToArrayAsync();
            Assert.Equal(20, headers.Length);
            Assert.Equal(10, headers.Count(item => item.Status == "Reserved"));
            Assert.Equal(10, headers.Count(item => item.Status == "Rejected"));
            Assert.Equal(20, await database.ReservationLines.CountAsync(item => ids.Contains(item.ReservationId)));
            Assert.Equal(10, await database.StockMovements.CountAsync(item => ids.Contains(item.OperationId)));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task T05_ReorderedReplayReturnsOriginalSuccessWithoutAnotherHold()
    {
        var first = await StockAsync(10);
        var second = await StockAsync(10);
        var id = Guid.NewGuid();
        using var created = await PostAsync(Request(id, (first, 3), (second, 2)));
        using var replay = await PostAsync(Request(id, (second, 2), (first, 3)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<ReservationResponse>())!.ReservationId);
        Assert.Equal(3, (await BalanceAsync(first)).Reserved);
        Assert.Equal(2, (await BalanceAsync(second)).Reserved);
        await using var database = fixture.CreateContext();
        Assert.Equal(2, await database.StockMovements.CountAsync(item => item.OperationId == id));
        Assert.Equal(2, await database.ReservationLines.CountAsync(item => item.ReservationId == id));
    }

    [Fact]
    public async Task ConcurrentSameIdHasOneSuccessfulStockEffect()
    {
        var product = await StockAsync(10);
        var id = Guid.NewGuid();
        var request = Request(id, (product, 1));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await start.Task;
            return await PostAsync(request);
        }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(tasks);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            InventoryAssertions.EqualBalance(new InventoryResponse(product, 10, 1, 9), await BalanceAsync(product));
            await using var database = fixture.CreateContext();
            Assert.Equal(1, await database.Reservations.CountAsync(item => item.Id == id));
            Assert.Equal(1, await database.ReservationLines.CountAsync(item => item.ReservationId == id));
            Assert.Equal(1, await database.StockMovements.CountAsync(item => item.OperationId == id));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task T15_RejectedIdStaysRejectedAfterRestockWithOriginalShortfalls()
    {
        var product = await StockAsync(2);
        var id = Guid.NewGuid();
        var request = Request(id, (product, 3));
        using var rejected = await PostAsync(request);
        await AssertProblemAsync(rejected, 409, "INSUFFICIENT_STOCK");
        var original = await ShortfallsAsync(rejected);
        await fixture.SetStockAsync(product, 10, "Replenished stock");
        var beforeReplay = await fixture.GetMovementsAsync(product);
        using var replay = await PostAsync(request);
        await AssertProblemAsync(replay, 409, "INSUFFICIENT_STOCK");
        Assert.Equal(original, await ShortfallsAsync(replay));
        Assert.Equal(new[] { new StockShortfall(product, 3, 2) }, original);
        InventoryAssertions.EqualBalance(new InventoryResponse(product, 10, 0, 10), await BalanceAsync(product));
        Assert.Equal(beforeReplay, await fixture.GetMovementsAsync(product));
        var outcome = await fixture.Client.GetFromJsonAsync<ReservationResponse>($"/api/inventory/reservations/{id}");
        Assert.Equal("Rejected", outcome!.Status);
        Assert.Equal("INSUFFICIENT_STOCK", outcome.FailureCode);
        Assert.Equal(original, outcome.Shortfalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentPayloadConflictsForEitherTerminalOutcome(bool rejected)
    {
        var product = await StockAsync(rejected ? 0 : 10);
        var id = Guid.NewGuid();
        using var original = await PostAsync(Request(id, (product, 1)));
        Assert.Equal(rejected ? HttpStatusCode.Conflict : HttpStatusCode.Created, original.StatusCode);
        using var conflict = await PostAsync(Request(id, (product, 2)));
        await AssertProblemAsync(conflict, 409, "RESERVATION_PAYLOAD_CONFLICT");
        Assert.Equal(rejected ? 0 : 1, (await BalanceAsync(product)).Reserved);
        await using var database = fixture.CreateContext();
        var line = await database.ReservationLines.SingleAsync(item => item.ReservationId == id);
        Assert.Equal(1, line.Quantity);
        Assert.Equal(rejected ? 0 : 1, await database.StockMovements.CountAsync(item => item.OperationId == id));
    }

    [Fact]
    public async Task MissingStockIsARecordedShortfallWithoutProductCallsOrStockCreation()
    {
        var product = Guid.NewGuid();
        var id = Guid.NewGuid();
        using var response = await PostAsync(Request(id, (product, 5)));
        await AssertProblemAsync(response, 409, "INSUFFICIENT_STOCK");
        Assert.Equal(new[] { new StockShortfall(product, 5, 0) }, await ShortfallsAsync(response));
        await using var database = fixture.CreateContext();
        Assert.False(await database.StockItems.AnyAsync(item => item.ProductId == product));
        Assert.False(await database.StockMovements.AnyAsync(item => item.OperationId == id));
        Assert.Equal(1, await database.ReservationLines.CountAsync(item => item.ReservationId == id));
        Assert.Equal(0, fixture.Products.CallCount(product));
    }

    [Fact]
    public async Task AllShortfallsAreRecordedInTheRejectedOutcome()
    {
        var first = await StockAsync(1);
        var second = await StockAsync(0);
        using var response = await PostAsync(Request(Guid.NewGuid(), (first, 2), (second, 3)));
        await AssertProblemAsync(response, 409, "INSUFFICIENT_STOCK");
        var shortfalls = await ShortfallsAsync(response);
        Assert.Equal(2, shortfalls.Length);
        Assert.Contains(new StockShortfall(first, 2, 1), shortfalls);
        Assert.Contains(new StockShortfall(second, 3, 0), shortfalls);
        Assert.Equal(0, (await BalanceAsync(first)).Reserved);
        Assert.Equal(0, (await BalanceAsync(second)).Reserved);
        Assert.Single(await fixture.GetMovementsAsync(first));
        Assert.Empty(await fixture.GetMovementsAsync(second));
    }

    [Fact]
    public async Task ThreeProductReservationSharesOneOperationIdAcrossThreeMovements()
    {
        var first = await StockAsync(5);
        var second = await StockAsync(5);
        var third = await StockAsync(5);
        var id = Guid.NewGuid();
        using var response = await PostAsync(Request(id, (first, 1), (second, 1), (third, 1)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var database = fixture.CreateContext();
        var rows = await database.StockMovements.Where(item => item.OperationId == id).ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.Equal(3, rows.Select(item => item.ProductId).Distinct().Count());
        Assert.All(rows, item => Assert.Equal(1, item.ReservedDelta));
    }

    [Fact]
    public async Task LedgerFailureRollsBackClaimLinesAndAllStockChanges()
    {
        var first = await StockAsync(10);
        var second = await StockAsync(10);
        var id = Guid.NewGuid();
        await using var database = fixture.CreateContext();
        await database.Database.ExecuteSqlRawAsync("""
            ALTER TABLE dbo.StockMovements WITH NOCHECK ADD CONSTRAINT CK_Test_RejectReserve
            CHECK ([MovementType] <> 'Reserve');
            """);
        try
        {
            using var response = await PostAsync(Request(id, (first, 2), (second, 3)));
            await AssertProblemAsync(response, 500, "INTERNAL_ERROR");
            InventoryAssertions.EqualBalance(new InventoryResponse(first, 10, 0, 10), await BalanceAsync(first));
            InventoryAssertions.EqualBalance(new InventoryResponse(second, 10, 0, 10), await BalanceAsync(second));
            Assert.False(await database.Reservations.AnyAsync(item => item.Id == id));
            Assert.False(await database.ReservationLines.AnyAsync(item => item.ReservationId == id));
            Assert.False(await database.StockMovements.AnyAsync(item => item.OperationId == id));
            using var get = await fixture.Client.GetAsync($"/api/inventory/reservations/{id}");
            await AssertProblemAsync(get, 404, "RESERVATION_NOT_FOUND");
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.StockMovements DROP CONSTRAINT CK_Test_RejectReserve;");
        }
    }

    [Fact]
    public async Task UnrelatedLedgerUniqueViolationIsNotTreatedAsAnIdempotentReplay()
    {
        var product = await StockAsync(10);
        var id = Guid.NewGuid();
        await using var database = fixture.CreateContext();
        database.StockMovements.Add(new StockMovement
        {
            OperationId = id, ProductId = product, MovementType = "Reserve",
            ReservedDelta = 1, OnHandAfter = 10, ReservedAfter = 1, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync();
        using var response = await PostAsync(Request(id, (product, 1)));
        await AssertProblemAsync(response, 500, "INTERNAL_ERROR");
        Assert.Equal(0, (await BalanceAsync(product)).Reserved);
        Assert.False(await database.Reservations.AnyAsync(item => item.Id == id));
        Assert.False(await database.ReservationLines.AnyAsync(item => item.ReservationId == id));
    }

    [Fact]
    public async Task FiftyDistinctLinesAreAcceptedAndPreservedOnRejection()
    {
        var items = Enumerable.Range(0, 50).Select(_ => (Guid.NewGuid(), 1)).ToArray();
        var id = Guid.NewGuid();
        using var response = await PostAsync(Request(id, items));
        await AssertProblemAsync(response, 409, "INSUFFICIENT_STOCK");
        Assert.Equal(50, (await ShortfallsAsync(response)).Length);
        await using var database = fixture.CreateContext();
        Assert.Equal(50, await database.ReservationLines.CountAsync(item => item.ReservationId == id));
        Assert.Equal("Rejected", (await database.Reservations.SingleAsync(item => item.Id == id)).Status);
    }

    [Fact]
    public async Task MaximumQuantityDoesNotOverflowReservedOrAvailable()
    {
        var product = await StockAsync(int.MaxValue);
        using var response = await PostAsync(Request(Guid.NewGuid(), (product, 10000)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        InventoryAssertions.EqualBalance(new InventoryResponse(product, int.MaxValue, 10000, int.MaxValue - 10000), await BalanceAsync(product));
    }

    [Fact]
    public async Task MissingReservationGetIsReadOnly()
    {
        var id = Guid.NewGuid();
        using var response = await fixture.Client.GetAsync($"/api/inventory/reservations/{id}");
        await AssertProblemAsync(response, 404, "RESERVATION_NOT_FOUND");
        await using var database = fixture.CreateContext();
        Assert.False(await database.Reservations.AnyAsync(item => item.Id == id));
        Assert.False(await database.StockMovements.AnyAsync(item => item.OperationId == id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public async Task SqlRejectsInvalidReservationLineQuantity(int quantity)
    {
        var id = Guid.NewGuid();
        await using var database = fixture.CreateContext();
        database.Reservations.Add(new Reservation
        {
            Id = id, RequestFingerprint = new string('a', 64), Status = "Reserved",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync();
        var productId = Guid.NewGuid();
        var exception = await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ReservationLines (ReservationId, ProductId, Quantity) VALUES ({id}, {productId}, {quantity})"));
        Assert.Equal(547, exception.Number);
    }

    [Fact]
    public async Task ARealSqlDeadlockRetriesTheWholeReservationWithTheSameId()
    {
        var products = new[] { await StockAsync(10), await StockAsync(10) }
            .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        var id = Guid.NewGuid();
        await using var blocker = fixture.CreateContext();
        await blocker.Database.OpenConnectionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY HIGH;");
        try
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE StockItems SET UpdatedAtUtc = {DateTimeOffset.UtcNow} WHERE ProductId = {products[1]}");
            var pending = PostAsync(Request(id, (products[0], 1), (products[1], 1)));
            await WaitForStockLockAsync(products[0]);
            await blocker.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE StockItems SET UpdatedAtUtc = {DateTimeOffset.UtcNow} WHERE ProductId = {products[0]}");
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(1, (await BalanceAsync(products[0])).Reserved);
            Assert.Equal(1, (await BalanceAsync(products[1])).Reserved);
            await using var verify = fixture.CreateContext();
            Assert.Equal(1, await verify.Reservations.CountAsync(item => item.Id == id && item.Status == "Reserved"));
            Assert.Equal(2, await verify.StockMovements.CountAsync(item => item.OperationId == id));
        }
        finally
        {
            await blocker.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY NORMAL;");
            await blocker.Database.CloseConnectionAsync();
        }
    }

    private async Task WaitForStockLockAsync(Guid productId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var probe = fixture.CreateContext();
            try
            {
                await probe.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT OnHand FROM StockItems WITH (UPDLOCK, NOWAIT) WHERE ProductId = {productId}", timeout.Token);
            }
            catch (SqlException exception) when (exception.Number == 1222)
            {
                return;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task<Guid> StockAsync(int quantity)
    {
        var id = fixture.AddProduct();
        await fixture.SetStockAsync(id, quantity);
        return id;
    }

    private async Task<InventoryResponse> BalanceAsync(Guid id)
    {
        return (await fixture.Client.GetFromJsonAsync<InventoryResponse>($"/api/inventory/{id}"))!;
    }

    private Task<HttpResponseMessage> PostAsync(CreateReservationRequest request)
    {
        return fixture.Client.PostAsJsonAsync("/api/inventory/reservations", request);
    }

    private static CreateReservationRequest Request(Guid id, params (Guid ProductId, int Quantity)[] items)
    {
        return new CreateReservationRequest
        {
            ReservationId = id,
            Items = items.Select(item => new ReservationItemRequest { ProductId = item.ProductId, Quantity = item.Quantity }).ToArray()
        };
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    private static async Task<StockShortfall[]> ShortfallsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("shortfalls").Deserialize<StockShortfall[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
