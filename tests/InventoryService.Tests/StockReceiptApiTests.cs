using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Inventory;
using Xunit;

namespace InventoryService.Tests;

[Trait("Category", "Integration")]
[Collection("Inventory SQL")]
public sealed class StockReceiptApiTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    [Fact]
    public async Task ReceiptAddsStockPreservesReservationsAndReplaysOriginalSnapshot()
    {
        var product = await StockAsync(400);
        using var reservation = await fixture.Client.PostAsJsonAsync("/api/inventory/reservations", new
        {
            reservationId = Guid.NewGuid(), items = new[] { new { productId = product, quantity = 20 } }
        });
        Assert.Equal(HttpStatusCode.Created, reservation.StatusCode);
        var request = Request(50, " Delivery 001 ");
        using var created = await PostAsync(product, request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = await created.Content.ReadFromJsonAsync<StockReceiptResponse>();
        Assert.Equal(new StockReceiptResponse(request.OperationId!.Value, product, 50, "Delivery 001",
            450, 20, 430, receipt!.CreatedAtUtc), receipt);
        await fixture.SetStockAsync(product, 500);
        using var replay = await PostAsync(product, Request(50, "Delivery 001", request.OperationId));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, await replay.Content.ReadFromJsonAsync<StockReceiptResponse>());
        InventoryAssertions.EqualBalance(new InventoryResponse(product, 500, 20, 480), await BalanceAsync(product));
        var movement = Assert.Single(await fixture.GetMovementsAsync(product), x => x.MovementType == "StockReceipt");
        Assert.Equal(request.OperationId, movement.OperationId);
        Assert.Equal(50, movement.OnHandDelta);
        Assert.Equal(0, movement.ReservedDelta);
        Assert.Equal(1, fixture.Products.CallCount(product));
    }

    [Fact]
    public async Task TwentyConcurrentDuplicatesAddStockOnce()
    {
        var product = await StockAsync(10);
        var request = Request(5);
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostAsync(product, request)));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Equal(19, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<StockReceiptResponse>()));
            Assert.All(bodies, body => Assert.Equal(bodies[0], body));
            Assert.Equal(15, (await BalanceAsync(product)).OnHand);
            Assert.Single(await fixture.GetMovementsAsync(product), x => x.MovementType == "StockReceipt");
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentDistinctReceiptsDoNotLoseUpdates()
    {
        var product = await StockAsync(10);
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostAsync(product, Request(2))));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            Assert.Equal(50, (await BalanceAsync(product)).OnHand);
            Assert.Equal(20, (await fixture.GetMovementsAsync(product)).Count(x => x.MovementType == "StockReceipt"));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("reason")]
    [InlineData("product")]
    public async Task SameOperationWithChangedPayloadConflicts(string changed)
    {
        var first = await StockAsync(10);
        var second = await StockAsync(10);
        var request = Request(5);
        using var created = await PostAsync(first, request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var conflict = await PostAsync(changed == "product" ? second : first,
            Request(changed == "quantity" ? 6 : 5, changed == "reason" ? "Other delivery" : "Delivery", request.OperationId));
        await AssertProblem(conflict, HttpStatusCode.Conflict, "RECEIPT_PAYLOAD_CONFLICT");
        Assert.Equal(15, (await BalanceAsync(first)).OnHand);
        Assert.Equal(10, (await BalanceAsync(second)).OnHand);
    }

    [Fact]
    public async Task ConcurrentReuseAcrossProductsRollsBackLosingStockUpdate()
    {
        var first = await StockAsync(10);
        var second = await StockAsync(10);
        var request = Request(5);
        var responses = await Task.WhenAll(PostAsync(first, request), PostAsync(second, request));
        using var response1 = responses[0];
        using var response2 = responses[1];
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        await AssertProblem(Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "RECEIPT_PAYLOAD_CONFLICT");
        Assert.Equal(25, (await BalanceAsync(first)).OnHand + (await BalanceAsync(second)).OnHand);
        await using var database = fixture.CreateContext();
        Assert.Equal(1, await database.StockMovements.CountAsync(x => x.OperationId == request.OperationId));
    }

    [Fact]
    public async Task OverflowRejectsWithoutBalanceOrLedgerChangesAndExactBoundarySucceeds()
    {
        var product = await StockAsync(int.MaxValue - 1);
        using var overflow = await PostAsync(product, Request(2));
        await AssertProblem(overflow, HttpStatusCode.Conflict, "STOCK_CAPACITY_EXCEEDED");
        Assert.Equal(int.MaxValue - 1, (await BalanceAsync(product)).OnHand);
        Assert.Single(await fixture.GetMovementsAsync(product));
        var request = Request(1);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(product, request)));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Equal(4, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(int.MaxValue, (await BalanceAsync(product)).OnHand);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task LedgerInsertFailureRollsBackStockAndTheSameIdCanBeRetried()
    {
        var product = await StockAsync(10);
        var request = Request(5);
        await using var database = fixture.CreateContext();
        var before = await database.StockItems.AsNoTracking().SingleAsync(x => x.ProductId == product);
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.StockMovements WITH NOCHECK ADD CONSTRAINT CK_TestReceiptFailure CHECK ([MovementType] <> 'StockReceipt');");
        try
        {
            using var failed = await PostAsync(product, request);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            var after = await database.StockItems.AsNoTracking().SingleAsync(x => x.ProductId == product);
            Assert.Equal(before.OnHand, after.OnHand);
            Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
            Assert.False(await database.StockMovements.AnyAsync(x => x.OperationId == request.OperationId));
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.StockMovements DROP CONSTRAINT CK_TestReceiptFailure;");
        }
        using var retry = await PostAsync(product, request);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(15, (await BalanceAsync(product)).OnHand);
    }

    [Fact]
    public async Task ReceiptAndReservationUpdateTheSameStockWithoutOverwritingEachOther()
    {
        var product = await StockAsync(10);
        var receiptTask = PostAsync(product, Request(5));
        var reservationTask = fixture.Client.PostAsJsonAsync("/api/inventory/reservations", new
        {
            reservationId = Guid.NewGuid(), items = new[] { new { productId = product, quantity = 10 } }
        });
        await Task.WhenAll(receiptTask, reservationTask);
        using var receipt = await receiptTask;
        using var reservation = await reservationTask;
        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        Assert.Equal(HttpStatusCode.Created, reservation.StatusCode);
        InventoryAssertions.EqualBalance(new InventoryResponse(product, 15, 10, 5), await BalanceAsync(product));
    }

    [Fact]
    public async Task MissingStockMustBeInitialisedBeforeReceipt()
    {
        var product = fixture.AddProduct();
        using var response = await PostAsync(product, Request(5));
        await AssertProblem(response, HttpStatusCode.NotFound, "STOCK_NOT_FOUND");
        Assert.Equal(0, fixture.Products.CallCount(product));
        await using var database = fixture.CreateContext();
        Assert.False(await database.StockMovements.AnyAsync(x => x.ProductId == product));
    }

    private async Task<Guid> StockAsync(int quantity)
    {
        var product = fixture.AddProduct();
        await fixture.SetStockAsync(product, quantity);
        return product;
    }

    private Task<InventoryResponse> BalanceAsync(Guid product) => fixture.Client.GetFromJsonAsync<InventoryResponse>($"/api/inventory/{product}")!;
    private Task<HttpResponseMessage> PostAsync(Guid product, ReceiveStockRequest request) => fixture.Client.PostAsJsonAsync($"/api/inventory/{product}/receipts", request);
    private static ReceiveStockRequest Request(int quantity, string reason = "Delivery", Guid? id = null) => new() { OperationId = id ?? Guid.NewGuid(), Quantity = quantity, Reason = reason };
    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("errorCode").GetString());
    }
}
