extern alias InventoryApi;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderEventWorker;
using Microsoft.Extensions.Logging.Abstractions;
using QubeERP.Messaging;
using Shared.Contracts.Events;
using Shared.Contracts.Orders;
using Xunit;

namespace OrderService.Tests;

[Trait("Category", "Integration")]
[Collection("Order SQL")]
public sealed class CancellationApiTests(RecoveryFixture fixture) : IClassFixture<RecoveryFixture>
{
    private Task<HttpResponseMessage> Cancel(Guid id, string reason = "Customer changed plans") =>
        fixture.Client.PostAsJsonAsync($"/api/orders/{id}/cancel", new CancelOrderRequest { Reason = reason });
    private Task<HttpResponseMessage> Reconcile(Guid id) =>
        fixture.Client.PostAsync($"/api/orders/{id}/reconcile-cancellation", null);
    private async Task<OrderResponse> Read(Guid id) => (await fixture.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}"))!;

    private async Task<(OrderResponse Order, string Key)> Create(params Guid[] products)
    {
        var key = Guid.NewGuid().ToString("N");
        using var created = await fixture.PostAsync(key, products.Select(id => new OrderItemRequest { ProductId = id, Quantity = 3 }).ToArray());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ((await created.Content.ReadFromJsonAsync<OrderResponse>())!, key);
    }

    [Fact]
    public async Task ConcurrentCancelReleasesEveryLineOnceKeepsFirstReasonAndCreatesOneEvent()
    {
        var first = await fixture.AddProductAsync(); var second = await fixture.AddProductAsync();
        var (order, key) = await Create(first, second);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dependencies.ReleaseResponse = async (id, reason, token) =>
        {
            Assert.Equal("Original reason", reason);
            if (!entered.Task.IsCompleted)
            {
                Assert.Equal("CancellationPending", (await Read(id)).Status);
                entered.TrySetResult();
            }
            await resume.Task;
            return await fixture.ReleaseAsync(id, reason, token);
        };
        try
        {
            var winner = Cancel(order.OrderId, "Original reason");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var competitors = Enumerable.Range(0, 8).Select(_ => Cancel(order.OrderId, "Ignored replacement")).ToArray();
            resume.SetResult();
            var responses = await Task.WhenAll(competitors.Prepend(winner));
            try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
            finally { foreach (var response in responses) response.Dispose(); }
        }
        finally { resume.TrySetResult(); fixture.ResetHandlers(); }
        using var replay = await Cancel(order.OrderId, "Another reason");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var saved = await Read(order.OrderId);
        Assert.Equal("Cancelled", saved.Status); Assert.Equal("Original reason", saved.CancellationReason);
        await AssertReleased(order.OrderId, first, second);
        await using var orders = fixture.CreateOrderContext();
        var events = await orders.OutboxMessages.Where(row => row.AggregateId == order.OrderId).ToArrayAsync();
        Assert.Equal(2, events.Length);
        var message = Assert.Single(events, row => row.EventType == "OrderCancelled");
        var payload = JsonSerializer.Deserialize<OrderIntegrationEvent>(message.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(payload.IsSupported()); Assert.Equal(1, payload.Version);
        var processor = new OrderEventProcessor(NullLogger<OrderEventProcessor>.Instance);
        Assert.True(processor.TryProcess(System.Text.Encoding.UTF8.GetBytes(message.PayloadJson),
            OrderEventTopology.RoutingKey(message.EventType), message.Id.ToString("D")));
        using var createReplay = await fixture.PostAsync(key,
            new OrderItemRequest { ProductId = first, Quantity = 3 }, new OrderItemRequest { ProductId = second, Quantity = 3 });
        Assert.Equal(HttpStatusCode.OK, createReplay.StatusCode);
        Assert.Equal("Cancelled", (await createReplay.Content.ReadFromJsonAsync<OrderResponse>())!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostReleaseResponseStaysPendingAndRecoversWithSingleRelease(bool reconcile)
    {
        var product = await fixture.AddProductAsync(); var (order, _) = await Create(product);
        fixture.Dependencies.ReleaseResponse = async (id, reason, token) =>
        {
            using var response = await fixture.ReleaseAsync(id, reason, token);
            throw new HttpRequestException("Release committed but response lost");
        };
        try
        {
            using var response = await Cancel(order.OrderId);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("CancellationPending", (await Read(order.OrderId)).Status);
        }
        finally { fixture.ResetHandlers(); }
        if (reconcile)
            fixture.Dependencies.ReleaseResponse = (_, _, _) => throw new InvalidOperationException("Released query must not post again");
        try
        {
            using var recovered = reconcile ? await Reconcile(order.OrderId) : await Cancel(order.OrderId, "Ignored");
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal("Cancelled", (await Read(order.OrderId)).Status);
            await AssertReleased(order.OrderId, product);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task ReconcileReservedRedrivesOriginalIdentityAndReason()
    {
        var product = await fixture.AddProductAsync(); var (order, _) = await Create(product);
        fixture.Dependencies.ReleaseResponse = (_, _, _) => throw new TaskCanceledException("Timeout before release");
        try
        {
            using var pending = await Cancel(order.OrderId, "Accepted reason");
            Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        }
        finally { fixture.ResetHandlers(); }
        using var recovered = await Reconcile(order.OrderId);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("Accepted reason", (await Read(order.OrderId)).CancellationReason);
        await AssertReleased(order.OrderId, product);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Rejected")]
    [InlineData("Fulfilled")]
    public async Task InvalidOrderStatesConflictWithoutCallingRelease(string status)
    {
        var product = await fixture.AddProductAsync(); var (order, _) = await Create(product);
        await using var database = fixture.CreateOrderContext();
        await database.Orders.Where(row => row.Id == order.OrderId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Status, status).SetProperty(row => row.FailureCode, status == "Rejected" ? "INVENTORY_INVALID_REQUEST" : null));
        fixture.Dependencies.ReleaseResponse = (_, _, _) => throw new InvalidOperationException("Must not release");
        try
        {
            using var response = await Cancel(order.OrderId);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(status, (await Read(order.OrderId)).Status);
            if (status == "Pending") Assert.Contains("Reconcile the reservation first", await response.Content.ReadAsStringAsync());
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task ConsumedRequiresReviewAndNothingIsReleasedOrAutomaticallyRecovered()
    {
        var product = await fixture.AddProductAsync(); var (order, _) = await Create(product);
        await using var inventory = fixture.CreateInventoryContext();
        await inventory.Reservations.Where(row => row.Id == order.OrderId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, "Consumed"));
        using var response = await Cancel(order.OrderId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("RequiresReview", (await Read(order.OrderId)).Status);
        Assert.Equal(3, (await inventory.StockItems.SingleAsync(row => row.ProductId == product)).Reserved);
        Assert.False(await inventory.StockMovements.AnyAsync(row => row.OperationId == order.OrderId && row.MovementType == "Release"));
        fixture.Dependencies.ReleaseResponse = (_, _, _) => throw new InvalidOperationException("Manual review only");
        fixture.Dependencies.InventoryQueryResponse = (_, _) => throw new InvalidOperationException("Manual review only");
        try
        {
            using var retry = await Cancel(order.OrderId); Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            using var reconcile = await Reconcile(order.OrderId); Assert.Equal(HttpStatusCode.Conflict, reconcile.StatusCode);
            var review = await fixture.Client.GetFromJsonAsync<OrderResponse[]>("/api/orders?status=RequiresReview");
            Assert.Contains(review!, row => row.OrderId == order.OrderId);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    [InlineData(422)]
    public async Task UnexpectedInventoryClientErrorsRequireReview(int status)
    {
        var product = await fixture.AddProductAsync(); var (order, _) = await Create(product);
        fixture.Dependencies.ReleaseResponse = (_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        try
        {
            using var response = await Cancel(order.OrderId);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("RequiresReview", (await Read(order.OrderId)).Status);
        }
        finally { fixture.ResetHandlers(); }
    }

    [Fact]
    public async Task OutboxFailureRollsBackOrderCompletionAndReconcileRecovers()
    {
        var product = await fixture.AddProductAsync(); var (order, _) = await Create(product);
        await using var database = fixture.CreateOrderContext();
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.OutboxMessages WITH NOCHECK ADD CONSTRAINT CK_TestCancellationFailure CHECK (EventType <> 'OrderCancelled')");
        try
        {
            using var failed = await Cancel(order.OrderId);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal("CancellationPending", (await Read(order.OrderId)).Status);
            Assert.False(await database.OutboxMessages.AnyAsync(row => row.AggregateId == order.OrderId && row.EventType == "OrderCancelled"));
        }
        finally { await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.OutboxMessages DROP CONSTRAINT CK_TestCancellationFailure"); }
        using var recovered = await Reconcile(order.OrderId);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await AssertReleased(order.OrderId, product);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidReasonsAre400(string? reason)
    {
        using var response = await fixture.Client.PostAsJsonAsync($"/api/orders/{Guid.NewGuid()}/cancel", new { reason });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownOrderIs404AndOverlongReasonIs400()
    {
        using var missing = await Cancel(Guid.NewGuid()); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalid = await Cancel(Guid.NewGuid(), new string('x', 501)); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private async Task AssertReleased(Guid id, params Guid[] products)
    {
        await using var inventory = fixture.CreateInventoryContext();
        Assert.Equal("Released", (await inventory.Reservations.AsNoTracking().SingleAsync(row => row.Id == id)).Status);
        Assert.Equal(products.Length, await inventory.StockMovements.CountAsync(row => row.OperationId == id && row.MovementType == "Release"));
        foreach (var product in products)
        {
            var stock = await inventory.StockItems.SingleAsync(row => row.ProductId == product);
            Assert.Equal(10, stock.OnHand); Assert.Equal(0, stock.Reserved);
        }
    }
}
