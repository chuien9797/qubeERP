using RabbitMQ.Client;

namespace QubeERP.Messaging;

public static class OrderEventTopology
{
    public const string Exchange = "qubeerp.orders";
    public const string Queue = "qubeerp.order-events";

    public static string RoutingKey(string eventType) => eventType switch
    {
        "OrderConfirmed" => "order.confirmed",
        "OrderRejected" => "order.rejected",
        "OrderCancelled" => "order.cancelled",
        _ => throw new ArgumentException("Unsupported order event type.", nameof(eventType))
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, "order.confirmed", cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, "order.rejected", cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, "order.cancelled", cancellationToken: cancellationToken);
    }
}
