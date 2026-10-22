using System.Text.Json;
using Microsoft.Extensions.Logging;
using QubeERP.Messaging;
using Shared.Contracts.Events;

namespace OrderEventWorker;

public sealed class OrderEventProcessor(ILogger<OrderEventProcessor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    public bool TryProcess(ReadOnlyMemory<byte> body, string routingKey, string? messageId)
    {
        if (body.Length > 16384) return false;
        OrderIntegrationEvent? message;
        try { message = JsonSerializer.Deserialize<OrderIntegrationEvent>(body.Span, JsonOptions); }
        catch (JsonException) { return false; }
        if (message is null || !message.IsSupported() || messageId != message.EventId.ToString("D")
            || routingKey != OrderEventTopology.RoutingKey(message.EventType)) return false;
        logger.LogInformation("Received {EventType} v{Version}: EventId={EventId}, OrderId={OrderId}, Status={Status}, OccurredAtUtc={OccurredAtUtc}",
            message.EventType, message.Version, message.EventId, message.OrderId, message.Status, message.OccurredAtUtc);
        return true;
    }
}
