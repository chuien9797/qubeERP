using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderEventWorker;
using OrderService.Data;
using OrderService.Messaging;
using QubeERP.Messaging;
using RabbitMQ.Client;
using Shared.Contracts.Events;
using Shared.Contracts.Inventory;
using Shared.Contracts.Orders;
using Xunit;

namespace OrderService.Tests;

[Trait("Category", "Integration")]
[Collection("Order SQL")]
public sealed class RabbitMqOutboxTests(OrderApiFixture orders, RabbitMqFixture rabbit)
    : IClassFixture<OrderApiFixture>, IClassFixture<RabbitMqFixture>
{
    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Rejected")]
    public async Task DurableEventsAreConfirmedRoutedAndMarkedPublished(string status)
    {
        var id = await CreateOrderAsync(status);
        await using var publisher = new RabbitMqEventPublisher(rabbit.Settings);
        await Dispatcher(publisher).DispatchBatchAsync(CancellationToken.None);
        await using var database = orders.CreateContext();
        var row = await database.OutboxMessages.SingleAsync(value => value.AggregateId == id);
        Assert.NotNull(row.PublishedAtUtc);
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.LastError);
        await using var connection = await rabbit.Settings.CreateFactory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var received = await GetEventAsync(channel, row.Id);
        Assert.Equal(OrderEventTopology.RoutingKey("Order" + status), received.RoutingKey);
        Assert.True(received.BasicProperties.Persistent);
        Assert.Equal(row.Id.ToString("D"), received.BasicProperties.MessageId);
        var message = JsonSerializer.Deserialize<OrderIntegrationEvent>(received.Body.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(message.IsSupported());
        Assert.Equal(id, message.OrderId);
    }

    [Fact]
    public async Task BrokerOutageDoesNotChangeOrderResultAndBacklogPublishesAfterRecovery()
    {
        await rabbit.StopBrokerAsync();
        try
        {
            var id = await CreateOrderAsync("Confirmed");
            await using var publisher = new RabbitMqEventPublisher(rabbit.Settings);
            var dispatcher = Dispatcher(publisher);
            await dispatcher.DispatchBatchAsync(CancellationToken.None);
            await using var database = orders.CreateContext();
            var failed = await database.OutboxMessages.AsNoTracking().SingleAsync(value => value.AggregateId == id);
            Assert.Null(failed.PublishedAtUtc);
            Assert.NotNull(failed.LastError);
            Assert.True(failed.AttemptCount >= 1);
            Assert.True(failed.NextAttemptAtUtc > failed.CreatedAtUtc);
            Assert.Equal("Confirmed", (await orders.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}"))!.Status);
            await rabbit.StartBrokerAsync();
            await database.OutboxMessages.Where(value => value.AggregateId == id)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.NextAttemptAtUtc, DateTimeOffset.UtcNow));
            await dispatcher.DispatchBatchAsync(CancellationToken.None);
            var after = await database.OutboxMessages.AsNoTracking().SingleAsync(value => value.AggregateId == id);
            Assert.NotNull(after.PublishedAtUtc);
            Assert.Null(after.LastError);
            Assert.True(after.AttemptCount >= 2);
            await using var connection = await rabbit.Settings.CreateFactory().CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            Assert.Equal(after.Id.ToString("D"), (await GetEventAsync(channel, after.Id)).BasicProperties.MessageId);
        }
        finally { await rabbit.StartBrokerAsync(); }
    }

    [Fact]
    public async Task PublisherFailureLeavesMessageUnpublishedAndDoesNotBusyRetry()
    {
        var id = await CreateOrderAsync("Confirmed");
        var publisher = new FailingPublisher();
        var dispatcher = Dispatcher(publisher);
        await dispatcher.DispatchBatchAsync(CancellationToken.None);
        var calls = publisher.Calls;
        await using (var waiting = orders.CreateContext())
            await waiting.OutboxMessages.Where(value => value.PublishedAtUtc == null).ExecuteUpdateAsync(update =>
                update.SetProperty(value => value.NextAttemptAtUtc, DateTimeOffset.UtcNow.AddMinutes(1)));
        await dispatcher.DispatchBatchAsync(CancellationToken.None);
        Assert.Equal(calls, publisher.Calls);
        await using var database = orders.CreateContext();
        var row = await database.OutboxMessages.SingleAsync(value => value.AggregateId == id);
        Assert.Null(row.PublishedAtUtc);
        Assert.NotNull(row.LastError);
        Assert.True(row.NextAttemptAtUtc > row.CreatedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealWorkerConsumesDuplicatesWithoutChangingBusinessStateAndReconnects(bool temporaryProcessingFailure)
    {
        var id = await CreateOrderAsync("Confirmed");
        await using var database = orders.CreateContext();
        var stored = await database.OutboxMessages.SingleAsync(value => value.AggregateId == id);
        var message = JsonSerializer.Deserialize<OrderIntegrationEvent>(stored.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var logger = new EventLogger(message.EventId, temporaryProcessingFailure);
        using var worker = new OrderEventConsumer(Options.Create(rabbit.Settings), new OrderEventProcessor(logger), NullLogger<OrderEventConsumer>.Instance);
        var inventoryCalls = orders.Dependencies.InventoryCalls;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await using var publisher = new RabbitMqEventPublisher(rabbit.Settings);
            await publisher.PublishAsync(message, CancellationToken.None);
            if (!temporaryProcessingFailure) await publisher.PublishAsync(message, CancellationToken.None);
            await logger.TwoEvents.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await rabbit.StopBrokerAsync();
            await rabbit.StartBrokerAsync();
            await PublishEventuallyAsync(publisher, message);
            await logger.ThreeEvents.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal("Confirmed", (await orders.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}"))!.Status);
            Assert.Equal(inventoryCalls, orders.Dependencies.InventoryCalls);
            Assert.Equal(1, await database.OutboxMessages.CountAsync(value => value.AggregateId == id));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private async Task<Guid> CreateOrderAsync(string status)
    {
        var product = orders.Dependencies.AddProduct();
        if (status == "Rejected") orders.Dependencies.InventoryResponse = (request, _) => Task.FromResult(DependencyStub.Json(HttpStatusCode.Conflict,
            new { errorCode = "INSUFFICIENT_STOCK", reservationId = request.ReservationId, shortfalls = new[] { new StockShortfall(product, 2, 1) } }));
        try
        {
            using var response = await orders.PostAsync(Guid.NewGuid().ToString("N"), new OrderItemRequest { ProductId = product, Quantity = 2 });
            Assert.Equal(status == "Confirmed" ? HttpStatusCode.Created : HttpStatusCode.Conflict, response.StatusCode);
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
            await using var database = orders.CreateContext();
            await database.OutboxMessages.Where(value => value.AggregateId != id && value.PublishedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.NextAttemptAtUtc, DateTimeOffset.UtcNow.AddHours(1)));
            return id;
        }
        finally { orders.Dependencies.InventoryResponse = null; }
    }

    private OutboxDispatcher Dispatcher(IOrderEventPublisher publisher) => new(new ContextFactory(orders), publisher,
        Options.Create(new OutboxSettings()), NullLogger<OutboxDispatcher>.Instance);

    [Fact]
    public async Task MandatoryUnroutableMessageIsNotMarkedPublished()
    {
        await using var publisher = new RabbitMqEventPublisher(rabbit.Settings);
        await publisher.PublishAsync(new OrderIntegrationEvent(Guid.NewGuid(), "OrderConfirmed", 1, Guid.NewGuid(), "Confirmed", DateTimeOffset.UtcNow), CancellationToken.None);
        await using var connection = await rabbit.Settings.CreateFactory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueUnbindAsync(OrderEventTopology.Queue, OrderEventTopology.Exchange, "order.confirmed");
        try
        {
            var id = await CreateOrderAsync("Confirmed");
            await Dispatcher(publisher).DispatchBatchAsync(CancellationToken.None);
            await using var database = orders.CreateContext();
            var row = await database.OutboxMessages.SingleAsync(value => value.AggregateId == id);
            Assert.Null(row.PublishedAtUtc);
            Assert.NotNull(row.LastError);
        }
        finally { await channel.QueueBindAsync(OrderEventTopology.Queue, OrderEventTopology.Exchange, "order.confirmed"); }
    }

    private static async Task PublishEventuallyAsync(IOrderEventPublisher publisher, OrderIntegrationEvent message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try { await publisher.PublishAsync(message, timeout.Token); return; }
            catch (Exception) when (!timeout.IsCancellationRequested) { await Task.Delay(250, timeout.Token); }
        }
    }

    private static async Task<BasicGetResult> GetEventAsync(IChannel channel, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await channel.BasicGetAsync(OrderEventTopology.Queue, autoAck: true, timeout.Token);
            if (message?.BasicProperties.MessageId == id.ToString("D")) return message;
            await Task.Delay(100, timeout.Token);
        }
    }

    private sealed class ContextFactory(OrderApiFixture fixture) : IDbContextFactory<OrderServiceContext>
    {
        public OrderServiceContext CreateDbContext() => fixture.CreateContext();
    }

    private sealed class FailingPublisher : IOrderEventPublisher
    {
        public int Calls { get; private set; }
        public Task PublishAsync(OrderIntegrationEvent message, CancellationToken cancellationToken)
        {
            Calls++;
            throw new IOException("Simulated broker confirmation failure");
        }
    }

    private sealed class EventLogger(Guid expectedId, bool failFirst) : ILogger<OrderEventProcessor>
    {
        private int count;
        public TaskCompletionSource TwoEvents { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThreeEvents { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains(expectedId.ToString(), StringComparison.Ordinal)) return;
            var observed = Interlocked.Increment(ref count);
            if (observed == 1 && failFirst) throw new IOException("Simulated temporary logging failure");
            if (observed >= 2) TwoEvents.TrySetResult();
            if (observed >= 3) ThreeEvents.TrySetResult();
        }
    }
}

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly string password = "Qube!" + Guid.NewGuid().ToString("N");
    public IContainer Container { get; private set; } = null!;
    public RabbitMqSettings Settings => new()
    {
        HostName = Container.Hostname, Port = Container.GetMappedPublicPort(5672),
        UserName = "qubeerp-test", Password = password, OperationTimeoutSeconds = 5
    };

    public async Task InitializeAsync()
    {
        Container = new ContainerBuilder("rabbitmq:4.2-management")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", "qubeerp-test").WithEnvironment("RABBITMQ_DEFAULT_PASS", password)
            .WithPortBinding(5672, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5672))
            .Build();
        await Container.StartAsync();
    }

    public async Task DisposeAsync() => await Container.DisposeAsync();

    public async Task StopBrokerAsync()
    {
        var result = await Container.ExecAsync(["rabbitmqctl", "stop_app"]);
        Assert.Equal(0, result.ExitCode);
    }

    public async Task StartBrokerAsync()
    {
        var result = await Container.ExecAsync(["rabbitmqctl", "start_app"]);
        Assert.Equal(0, result.ExitCode);
    }
}
