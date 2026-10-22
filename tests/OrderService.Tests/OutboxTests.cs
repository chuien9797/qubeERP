using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Events;
using Shared.Contracts.Inventory;
using Shared.Contracts.Orders;
using Xunit;

namespace OrderService.Tests;

[Trait("Category", "Integration")]
[Collection("Order SQL")]
public sealed class OutboxTests(OrderApiFixture fixture) : IClassFixture<OrderApiFixture>
{
    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Rejected")]
    public async Task TerminalTransitionCreatesOneMinimalEventAndReplayDoesNotCreateAnother(string status)
    {
        var product = fixture.Dependencies.AddProduct();
        var key = Guid.NewGuid().ToString("N");
        if (status == "Rejected") fixture.Dependencies.InventoryResponse = (request, _) => Task.FromResult(Shortage(request, product));
        try
        {
            using var created = await fixture.PostAsync(key, Item(product));
            Assert.Equal(status == "Confirmed" ? HttpStatusCode.Created : HttpStatusCode.Conflict, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
            await using var database = fixture.CreateContext();
            var stored = Assert.Single(await database.OutboxMessages.Where(value => value.AggregateId == id).ToArrayAsync());
            var message = JsonSerializer.Deserialize<OrderIntegrationEvent>(stored.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.True(message.IsSupported());
            Assert.Equal(stored.Id, message.EventId);
            Assert.Equal(id, message.OrderId);
            Assert.Equal("Order" + status, message.EventType);
            Assert.Null(stored.PublishedAtUtc);
            Assert.Equal(0, stored.AttemptCount);
            using var json = JsonDocument.Parse(stored.PayloadJson);
            Assert.Equal(6, json.RootElement.EnumerateObject().Count());
            var calls = fixture.Dependencies.InventoryCalls;
            using var replay = await fixture.PostAsync(key, Item(product));
            Assert.Equal(status == "Confirmed" ? HttpStatusCode.OK : HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Equal(calls, fixture.Dependencies.InventoryCalls);
            Assert.Equal(1, await database.OutboxMessages.CountAsync(value => value.AggregateId == id));
        }
        finally { fixture.Dependencies.InventoryResponse = null; }
    }

    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Rejected")]
    public async Task PendingRecoveryCreatesOnlyTheFirstTerminalEvent(string status)
    {
        var product = fixture.Dependencies.AddProduct();
        var key = Guid.NewGuid().ToString("N");
        fixture.Dependencies.InventoryResponse = (_, _) => throw new HttpRequestException("Response unavailable");
        Guid id;
        try
        {
            using var pending = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
            id = (await pending.Content.ReadFromJsonAsync<PendingOrderResponse>())!.OrderId;
        }
        finally { fixture.Dependencies.InventoryResponse = null; }
        await using var database = fixture.CreateContext();
        Assert.False(await database.OutboxMessages.AnyAsync(value => value.AggregateId == id));
        fixture.Dependencies.Outcomes[id] = new ReservationResponse(id, status == "Confirmed" ? "Reserved" : "Rejected",
            status == "Confirmed" ? null : "INSUFFICIENT_STOCK", status == "Confirmed" ? [] : [new StockShortfall(product, 2, 1)]);
        var calls = fixture.Dependencies.InventoryCalls;
        using var recovered = await fixture.PostAsync(key, Item(product));
        Assert.Equal(status == "Confirmed" ? HttpStatusCode.OK : HttpStatusCode.Conflict, recovered.StatusCode);
        using var replay = await fixture.PostAsync(key, Item(product));
        Assert.Equal(1, await database.OutboxMessages.CountAsync(value => value.AggregateId == id));
        Assert.Equal(calls, fixture.Dependencies.InventoryCalls);
    }

    [Fact]
    public async Task ConcurrentSameKeyRequestsCreateOneEvent()
    {
        var product = fixture.Dependencies.AddProduct();
        var key = Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.PostAsync(key, Item(product))));
        try
        {
            Assert.All(responses, response => Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Accepted));
            await using var database = fixture.CreateContext();
            var order = await database.Orders.SingleAsync(value => value.IdempotencyKey == key);
            Assert.Equal("Confirmed", order.Status);
            Assert.Equal(1, await database.OutboxMessages.CountAsync(value => value.AggregateId == order.Id));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task OutboxFailureRollsBackTransitionAndRecoveryUsesExistingReservation()
    {
        var product = fixture.Dependencies.AddProduct();
        var key = Guid.NewGuid().ToString("N");
        await using var database = fixture.CreateContext();
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.OutboxMessages WITH NOCHECK ADD CONSTRAINT CK_TestOutboxFailure CHECK ([EventVersion] <> 1);");
        Guid id;
        try
        {
            using var response = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var pending = await database.Orders.AsNoTracking().SingleAsync(value => value.IdempotencyKey == key);
            id = pending.Id;
            Assert.Equal("Pending", pending.Status);
            Assert.False(await database.OutboxMessages.AnyAsync(value => value.AggregateId == id));
        }
        finally { await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.OutboxMessages DROP CONSTRAINT CK_TestOutboxFailure;"); }
        var calls = fixture.Dependencies.InventoryCalls;
        using var recovered = await fixture.PostAsync(key, Item(product));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(calls, fixture.Dependencies.InventoryCalls);
        Assert.Equal(1, await database.OutboxMessages.CountAsync(value => value.AggregateId == id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainAndRequiresReviewOutcomesDoNotCreateEvents(bool review)
    {
        var product = fixture.Dependencies.AddProduct();
        var key = Guid.NewGuid().ToString("N");
        fixture.Dependencies.InventoryResponse = (_, _) => review
            ? Task.FromResult(DependencyStub.Json(HttpStatusCode.OK, new { status = "unexpected" }))
            : throw new HttpRequestException("Uncertain response");
        try
        {
            using var response = await fixture.PostAsync(key, Item(product));
            Assert.Equal(review ? HttpStatusCode.BadGateway : HttpStatusCode.Accepted, response.StatusCode);
            await using var database = fixture.CreateContext();
            var order = await database.Orders.SingleAsync(value => value.IdempotencyKey == key);
            Assert.False(await database.OutboxMessages.AnyAsync(value => value.AggregateId == order.Id));
        }
        finally { fixture.Dependencies.InventoryResponse = null; }
    }

    private static OrderItemRequest Item(Guid product) => new() { ProductId = product, Quantity = 2 };
    private static HttpResponseMessage Shortage(CreateReservationRequest request, Guid product) => DependencyStub.Json(HttpStatusCode.Conflict,
        new { errorCode = "INSUFFICIENT_STOCK", reservationId = request.ReservationId, shortfalls = new[] { new StockShortfall(product, 2, 1) } });
}
