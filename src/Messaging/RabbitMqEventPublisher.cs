using System.Text.Json;
using RabbitMQ.Client;
using Shared.Contracts.Events;

namespace QubeERP.Messaging;

public sealed class RabbitMqEventPublisher(RabbitMqSettings settings) : IOrderEventPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IConnection? connection;
    private IChannel? channel;

    public async Task PublishAsync(OrderIntegrationEvent message, CancellationToken cancellationToken)
    {
        if (!message.IsSupported()) throw new ArgumentException("Unsupported order event.", nameof(message));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.OperationTimeoutSeconds));
        try
        {
            if (channel is null || !channel.IsOpen)
            {
                await DisposeAsync();
                connection = await settings.CreateFactory().CreateConnectionAsync("QubeERP outbox", timeout.Token);
                channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), timeout.Token);
                await OrderEventTopology.DeclareAsync(channel, timeout.Token);
            }
            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.EventId.ToString("D"),
                CorrelationId = message.OrderId.ToString("D"),
                Type = message.EventType
            };
            await channel.BasicPublishAsync(OrderEventTopology.Exchange, OrderEventTopology.RoutingKey(message.EventType),
                mandatory: true, properties, JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions), timeout.Token);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var previousChannel = channel;
        var previousConnection = connection;
        channel = null;
        connection = null;
        try
        {
            if (previousChannel is not null) await previousChannel.DisposeAsync();
        }
        finally
        {
            if (previousConnection is not null) await previousConnection.DisposeAsync();
        }
    }
}
