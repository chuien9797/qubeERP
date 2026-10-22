using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Inventory;
using Shared.Contracts.Orders;
using Xunit;

namespace OrderService.Tests;

[Trait("Category", "Integration")]
[Collection("Order SQL")]
public sealed class PendingRecoveryTests(RecoveryFixture fixture) : IClassFixture<RecoveryFixture>
{
    private DependencyStub Dependencies => fixture.Dependencies;
    private static string NewKey() => Guid.NewGuid().ToString("N");
    private static OrderItemRequest Item(Guid id, int quantity = 1) => new() { ProductId = id, Quantity = quantity };

    [Fact]
    public async Task DeactivationAfterValidationDoesNotFenceAnInFlightInventoryCommit()
    {
        var product = await fixture.AddProductAsync(onHand: 10);
        // Product HTTP lookup has already returned its active snapshot before this handler runs.
        Dependencies.InventoryResponse = async (request, token) =>
        {
            Dependencies.Products[product] = Dependencies.Products[product] with { IsActive = false };
            return await fixture.ReserveAsync(request, token);
        };
        try
        {
            using var inFlight = await fixture.PostAsync(NewKey(), Item(product, 2));
            Assert.Equal(HttpStatusCode.Created, inFlight.StatusCode);
            var saved = (await inFlight.Content.ReadFromJsonAsync<OrderResponse>())!;
            Assert.Equal("Confirmed", saved.Status);
            var calls = Dependencies.InventoryCalls;
            using var later = await fixture.PostAsync(NewKey(), Item(product, 1));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, later.StatusCode);
            Assert.Equal(calls, Dependencies.InventoryCalls);
            await AssertHoldAsync(saved.OrderId, product, 10, 2, 1);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task T09_LostResponseAfterCommittedHoldRecoversWithoutAnotherReservation()
    {
        var first = await fixture.AddProductAsync(price: 12.50m);
        var second = await fixture.AddProductAsync(price: 7.25m);
        var key = NewKey();
        Dependencies.InventoryResponse = async (request, token) =>
        {
            using var committed = await fixture.ReserveAsync(request, token);
            Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
            throw new HttpRequestException("Response lost after inventory commit");
        };
        try
        {
            using var response = await fixture.PostAsync(key, Item(first, 3), Item(second, 2));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var pending = (await response.Content.ReadFromJsonAsync<PendingOrderResponse>())!;
            await AssertHoldAsync(pending.OrderId, first, 10, 3, 2);
            var queries = Dependencies.InventoryQueryCalls;
            var before = await ReadAsync(pending.OrderId);
            Assert.Equal("Pending", before.Status);
            Assert.Equal(queries, Dependencies.InventoryQueryCalls);
            fixture.ResetHandlers();
            Dependencies.Products[first] = Dependencies.Products[first] with { UnitPrice = 999m, Name = "Changed", IsActive = false };
            Dependencies.ProductResponse = (_, _) => throw new HttpRequestException("Product unavailable");
            var calls = Dependencies.InventoryCalls;
            var productCalls = Dependencies.ProductCalls;
            using var recovered = await fixture.PostAsync(key, Item(second, 2), Item(first, 3));
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            var order = (await recovered.Content.ReadFromJsonAsync<OrderResponse>())!;
            Assert.Equal(pending.OrderId, order.OrderId);
            Assert.Equal("Confirmed", order.Status);
            Assert.Equal(52m, order.TotalAmount);
            Assert.Equal("Original product", order.Lines[0].Name);
            Assert.Equal(12.50m, order.Lines.Single(line => line.ProductId == first).UnitPrice);
            Assert.Equal(before.Lines.ToArray(), order.Lines.ToArray());
            Assert.Equal(productCalls, Dependencies.ProductCalls);
            Assert.Equal(calls, Dependencies.InventoryCalls);
            Assert.Equal(queries + 1, Dependencies.InventoryQueryCalls);
            await AssertHoldAsync(order.OrderId, first, 10, 3, 2);
            using var replay = await fixture.PostAsync(key, Item(first, 3), Item(second, 2));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(queries + 1, Dependencies.InventoryQueryCalls);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task LostRejectionIsRecoveredWithOriginalShortfallsAfterRestock()
    {
        var first = await fixture.AddProductAsync();
        var second = await fixture.AddProductAsync(onHand: 1);
        var key = NewKey();
        Dependencies.InventoryResponse = async (request, token) =>
        {
            using var committed = await fixture.ReserveAsync(request, token);
            Assert.Equal(HttpStatusCode.Conflict, committed.StatusCode);
            throw new HttpRequestException("Rejected response lost");
        };
        try
        {
            using var response = await fixture.PostAsync(key, Item(first, 3), Item(second, 2));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var pending = (await response.Content.ReadFromJsonAsync<PendingOrderResponse>())!;
            await using var inventory = fixture.CreateInventoryContext();
            await inventory.StockItems.Where(stock => stock.ProductId == second).ExecuteUpdateAsync(update => update.SetProperty(stock => stock.OnHand, 10));
            fixture.ResetHandlers();
            var calls = Dependencies.InventoryCalls;
            using var recovered = await fixture.PostAsync(key, Item(first, 3), Item(second, 2));
            Assert.Equal(HttpStatusCode.Conflict, recovered.StatusCode);
            var saved = await ReadAsync(pending.OrderId);
            Assert.Equal("Rejected", saved.Status);
            Assert.Equal(new StockShortfall(second, 2, 1), Assert.Single(saved.Shortfalls));
            Assert.Equal(calls, Dependencies.InventoryCalls);
            Assert.False(await inventory.StockMovements.AnyAsync(movement => movement.OperationId == saved.OrderId));
            Assert.All(await inventory.StockItems.Where(stock => stock.ProductId == first || stock.ProductId == second).ToListAsync(), stock => Assert.Equal(0, stock.Reserved));
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task MissingReservationIsRepostedUsingSavedIdAndLines()
    {
        var product = await fixture.AddProductAsync(price: 3.33m);
        var key = NewKey();
        var pending = await CreatePendingAsync(key, Item(product, 3));
        var calls = Dependencies.InventoryCalls;
        Dependencies.Products[product] = Dependencies.Products[product] with { UnitPrice = 999m, IsActive = false };
        Dependencies.ProductResponse = (_, _) => throw new HttpRequestException("Product unavailable");
        try
        {
            using var recovered = await fixture.PostAsync(key, Item(product, 3));
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            var saved = (await recovered.Content.ReadFromJsonAsync<OrderResponse>())!;
            Assert.Equal(pending.OrderId, saved.OrderId);
            Assert.Equal(9.99m, saved.TotalAmount);
            Assert.Equal(calls + 1, Dependencies.InventoryCalls);
            Assert.All(Dependencies.Reservations.Where(request => request.ReservationId == saved.OrderId), request =>
                Assert.Equal(3, Assert.Single(request.Items!).Quantity));
            await AssertHoldAsync(saved.OrderId, product, 10, 3, 1);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("disconnect")]
    [InlineData("server-error")]
    [InlineData("http-timeout")]
    public async Task UncertainQueryKeepsPendingWithoutRepostingAndLaterRecovers(string failure)
    {
        var product = await fixture.AddProductAsync();
        var key = NewKey();
        var pending = await CreatePendingAsync(key, Item(product, 2));
        var calls = Dependencies.InventoryCalls;
        Dependencies.InventoryQueryResponse = (_, _) => failure switch
        {
            "timeout" => throw new TaskCanceledException("Query timeout"),
            "disconnect" => throw new HttpRequestException("Query disconnected"),
            "http-timeout" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout)),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };
        try
        {
            using var replay = await fixture.PostAsync(key, Item(product, 2));
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            var saved = await ReadAsync(pending.OrderId);
            Assert.Equal("Pending", saved.Status);
            Assert.Equal(calls, Dependencies.InventoryCalls);
            fixture.ResetHandlers();
            using var recovered = await fixture.PostAsync(key, Item(product, 2));
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            await AssertHoldAsync(pending.OrderId, product, 10, 2, 1);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Theory]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(400, "INVALID_RESERVATION")]
    [InlineData(409, "RESERVATION_PAYLOAD_CONFLICT")]
    [InlineData(422, "UNKNOWN")]
    public async Task UnexpectedQueryRequiresReviewWithoutBlindReservation(int status, string code)
    {
        var product = await fixture.AddProductAsync();
        var key = NewKey();
        var pending = await CreatePendingAsync(key, Item(product));
        var calls = Dependencies.InventoryCalls;
        Dependencies.InventoryQueryResponse = (id, _) => Task.FromResult(DependencyStub.Json((HttpStatusCode)status,
            new { errorCode = code, reservationId = id }));
        try
        {
            using var replay = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.BadGateway, replay.StatusCode);
            var saved = await ReadAsync(pending.OrderId);
            Assert.Equal("RequiresReview", saved.Status);
            Assert.Equal("INVENTORY_UNEXPECTED_RESPONSE", saved.FailureCode);
            Assert.Equal(calls, Dependencies.InventoryCalls);
            var queries = Dependencies.InventoryQueryCalls;
            using var terminalReplay = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.BadGateway, terminalReplay.StatusCode);
            Assert.Equal(queries, Dependencies.InventoryQueryCalls);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task RepostResponseLossStaysPendingUntilSavedOutcomeCanBeRead()
    {
        var product = await fixture.AddProductAsync();
        var key = NewKey();
        var pending = await CreatePendingAsync(key, Item(product, 2));
        Dependencies.InventoryResponse = async (request, token) =>
        {
            using var committed = await fixture.ReserveAsync(request, token);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };
        try
        {
            using var retry = await fixture.PostAsync(key, Item(product, 2));
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            Assert.Equal("Pending", (await ReadAsync(pending.OrderId)).Status);
            await AssertHoldAsync(pending.OrderId, product, 10, 2, 1);
            fixture.ResetHandlers();
            var calls = Dependencies.InventoryCalls;
            using var recovered = await fixture.PostAsync(key, Item(product, 2));
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(calls, Dependencies.InventoryCalls);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task T19_ConcurrentRecoveryAndOriginalInFlightOperationHaveOneStockEffect()
    {
        var first = await fixture.AddProductAsync();
        var second = await fixture.AddProductAsync();
        var key = NewKey();
        var originalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? original = null;
        Dependencies.InventoryResponse = (request, _) =>
        {
            original = Task.Run(async () =>
            {
                await originalGate.Task;
                using var response = await fixture.ReserveAsync(request, CancellationToken.None);
                Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
            });
            throw new HttpRequestException("Original call disconnected while operation is in flight");
        };
        try
        {
            using var initial = await fixture.PostAsync(key, Item(first, 3), Item(second, 2));
            Assert.Equal(HttpStatusCode.Accepted, initial.StatusCode);
            var pending = (await initial.Content.ReadFromJsonAsync<PendingOrderResponse>())!;
            var arrivals = 0;
            var queriesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dependencies.InventoryResponse = fixture.ReserveAsync;
            Dependencies.InventoryQueryResponse = async (id, token) =>
            {
                var absent = await fixture.QueryAsync(id, token);
                Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
                if (Interlocked.Increment(ref arrivals) == 2)
                {
                    originalGate.TrySetResult();
                    queriesReady.TrySetResult();
                }
                await queriesReady.Task.WaitAsync(TimeSpan.FromSeconds(4), token);
                return absent;
            };
            var responses = await Task.WhenAll(fixture.PostAsync(key, Item(first, 3), Item(second, 2)),
                fixture.PostAsync(key, Item(second, 2), Item(first, 3)));
            try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
            finally { foreach (var response in responses) response.Dispose(); }
            await original!.WaitAsync(TimeSpan.FromSeconds(15));
            var saved = await ReadAsync(pending.OrderId);
            Assert.Equal("Confirmed", saved.Status);
            await using var orders = fixture.CreateOrderContext();
            Assert.Equal(1, await orders.Orders.CountAsync(order => order.IdempotencyKey == key));
            Assert.Equal(2, await orders.OrderLines.CountAsync(line => line.OrderId == saved.OrderId));
            await AssertHoldAsync(saved.OrderId, first, 10, 3, 2);
            await AssertHoldAsync(saved.OrderId, second, 10, 2, 2);
        }
        finally
        {
            originalGate.TrySetResult();
            fixture.ResetHandlers();
            if (original is not null) await original.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task RecoveryAfterOrderUpdateFailureReadsTheCommittedReservation()
    {
        var product = await fixture.AddProductAsync();
        var key = NewKey();
        await using var orders = fixture.CreateOrderContext();
        await orders.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.Orders WITH NOCHECK ADD CONSTRAINT CK_TestConfirmFailure CHECK (Status <> 'Confirmed')");
        try
        {
            using var initial = await fixture.PostAsync(key, Item(product, 2));
            Assert.Equal(HttpStatusCode.InternalServerError, initial.StatusCode);
        }
        finally { await orders.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.Orders DROP CONSTRAINT CK_TestConfirmFailure"); }
        var saved = await orders.Orders.AsNoTracking().SingleAsync(order => order.IdempotencyKey == key);
        Assert.Equal("Pending", saved.Status);
        await AssertHoldAsync(saved.Id, product, 10, 2, 1);
        var calls = Dependencies.InventoryCalls;
        using var recovered = await fixture.PostAsync(key, Item(product, 2));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(calls, Dependencies.InventoryCalls);
        Assert.Equal("Confirmed", (await ReadAsync(saved.Id)).Status);
    }

    [Fact]
    public async Task DifferentPayloadOnPendingKeyDoesNotStartRecovery()
    {
        var product = await fixture.AddProductAsync();
        var key = NewKey();
        var pending = await CreatePendingAsync(key, Item(product));
        var calls = Dependencies.InventoryCalls;
        var queries = Dependencies.InventoryQueryCalls;
        var products = Dependencies.ProductCalls;
        using var conflict = await fixture.PostAsync(key, Item(product, 2));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(calls, Dependencies.InventoryCalls);
        Assert.Equal(queries, Dependencies.InventoryQueryCalls);
        Assert.Equal(products, Dependencies.ProductCalls);
        Assert.Equal("Pending", (await ReadAsync(pending.OrderId)).Status);
    }

    [Fact]
    public async Task RecoveryCannotReplaceAConcurrentTerminalState()
    {
        var product = await fixture.AddProductAsync();
        var key = NewKey();
        var pending = await CreatePendingAsync(key, Item(product));
        Dependencies.InventoryQueryResponse = async (id, token) =>
        {
            await using var orders = fixture.CreateOrderContext();
            await orders.Orders.Where(order => order.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(order => order.Status, "RequiresReview").SetProperty(order => order.FailureCode, "OPERATOR_REVIEW"), token);
            return DependencyStub.Json(HttpStatusCode.OK, new ReservationDetailsResponse(id, "Reserved", null, [], [new(product, 1)]));
        };
        try
        {
            using var recovered = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.BadGateway, recovered.StatusCode);
            var saved = await ReadAsync(pending.OrderId);
            Assert.Equal("RequiresReview", saved.Status);
            Assert.Equal("OPERATOR_REVIEW", saved.FailureCode);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task RecoveryCannotConfirmAReservationWithDifferentPersistedQuantities()
    {
        var product = await fixture.AddProductAsync();
        var key = NewKey();
        var pending = await CreatePendingAsync(key, Item(product, 2));
        using var unrelatedHold = await fixture.ReserveAsync(new CreateReservationRequest
        {
            ReservationId = pending.OrderId,
            Items = [new() { ProductId = product, Quantity = 1 }]
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, unrelatedHold.StatusCode);
        var calls = Dependencies.InventoryCalls;
        using var replay = await fixture.PostAsync(key, Item(product, 2));
        Assert.Equal(HttpStatusCode.BadGateway, replay.StatusCode);
        Assert.Equal("RequiresReview", (await ReadAsync(pending.OrderId)).Status);
        Assert.Equal(calls, Dependencies.InventoryCalls);
        await AssertHoldAsync(pending.OrderId, product, 10, 1, 1);
    }

    [Fact]
    public async Task HttpTimeoutAfterCommittedReservationRemainsPendingAndRecoversWithoutASecondHold()
    {
        var product = await fixture.AddProductAsync();
        var key = NewKey();
        Dependencies.InventoryResponse = async (request, token) =>
        {
            using var committed = await fixture.ReserveAsync(request, token);
            Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        };
        try
        {
            using var initial = await fixture.PostAsync(key, Item(product, 2));
            Assert.Equal(HttpStatusCode.Accepted, initial.StatusCode);
            var pending = (await initial.Content.ReadFromJsonAsync<PendingOrderResponse>())!;
            var calls = Dependencies.InventoryCalls;
            fixture.ResetHandlers();
            using var replay = await fixture.PostAsync(key, Item(product, 2));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal("Confirmed", (await ReadAsync(pending.OrderId)).Status);
            Assert.Equal(calls, Dependencies.InventoryCalls);
            await AssertHoldAsync(pending.OrderId, product, 10, 2, 1);
        }
        finally { fixture.ResetHandlers(); }
    }

    private async Task<PendingOrderResponse> CreatePendingAsync(string key, params OrderItemRequest[] items)
    {
        Dependencies.InventoryResponse = (_, _) => throw new HttpRequestException("Inventory did not receive the request");
        try
        {
            using var response = await fixture.PostAsync(key, items);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PendingOrderResponse>())!;
        }
        finally { fixture.ResetHandlers(); }
    }

    private async Task<OrderResponse> ReadAsync(Guid id) =>
        (await fixture.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}"))!;

    private async Task AssertHoldAsync(Guid orderId, Guid productId, int onHand, int reserved, int movementCount)
    {
        await using var inventory = fixture.CreateInventoryContext();
        var stock = await inventory.StockItems.SingleAsync(value => value.ProductId == productId);
        Assert.Equal(onHand, stock.OnHand);
        Assert.Equal(reserved, stock.Reserved);
        Assert.Equal(movementCount, await inventory.StockMovements.CountAsync(movement => movement.OperationId == orderId && movement.MovementType == "Reserve"));
        Assert.Equal(1, await inventory.Reservations.CountAsync(reservation => reservation.Id == orderId));
    }
}
