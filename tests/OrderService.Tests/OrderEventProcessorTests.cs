using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderEventWorker;
using QubeERP.Messaging;
using Shared.Contracts.Events;
using Xunit;

namespace OrderService.Tests;

public sealed class OrderEventProcessorTests
{
    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Rejected")]
    public void SupportedEventsAreLoggedAndDuplicateDeliveryOnlyRepeatsTheLog(string status)
    {
        var logger = new RecordingLogger();
        var processor = new OrderEventProcessor(logger);
        var message = Event(status);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(processor.TryProcess(payload, OrderEventTopology.RoutingKey(message.EventType), message.EventId.ToString("D")));
        Assert.True(processor.TryProcess(payload, OrderEventTopology.RoutingKey(message.EventType), message.EventId.ToString("D")));
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, line => Assert.Contains(message.OrderId.ToString(), line));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("status")]
    [InlineData("type")]
    [InlineData("empty-event-id")]
    [InlineData("empty-order-id")]
    [InlineData("timestamp")]
    [InlineData("routing-key")]
    [InlineData("message-id")]
    [InlineData("json")]
    [InlineData("oversized")]
    [InlineData("missing-fields")]
    public void InvalidEventsAreNotProcessed(string failure)
    {
        var logger = new RecordingLogger();
        var processor = new OrderEventProcessor(logger);
        var message = Event("Confirmed");
        message = failure switch
        {
            "version" => message with { Version = 2 },
            "status" => message with { Status = "Pending" },
            "type" => message with { EventType = "OrderCreated" },
            "empty-event-id" => message with { EventId = Guid.Empty },
            "empty-order-id" => message with { OrderId = Guid.Empty },
            "timestamp" => message with { OccurredAtUtc = default },
            _ => message
        };
        var payload = failure switch
        {
            "json" => "{invalid"u8.ToArray(),
            "oversized" => new byte[16385],
            "missing-fields" => "{}"u8.ToArray(),
            _ => JsonSerializer.SerializeToUtf8Bytes(message)
        };
        Assert.False(processor.TryProcess(payload, failure == "routing-key" ? "order.rejected" : "order.confirmed",
            failure == "message-id" ? Guid.NewGuid().ToString("D") : message.EventId.ToString("D")));
        Assert.Empty(logger.Messages);
    }

    private static OrderIntegrationEvent Event(string status) => new(Guid.NewGuid(), "Order" + status, 1, Guid.NewGuid(), status, DateTimeOffset.UtcNow);

    private sealed class RecordingLogger : ILogger<OrderEventProcessor>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
