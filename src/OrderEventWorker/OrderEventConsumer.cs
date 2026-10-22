using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QubeERP.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderEventWorker;

public sealed class OrderEventConsumer(IOptions<RabbitMqSettings> options, OrderEventProcessor processor,
    ILogger<OrderEventConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(options.Value.OperationTimeoutSeconds));
                await using var connection = await options.Value.CreateFactory().CreateConnectionAsync("QubeERP order-event worker", connectTimeout.Token);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: connectTimeout.Token);
                await OrderEventTopology.DeclareAsync(channel, connectTimeout.Token);
                await channel.BasicQosAsync(0, 1, false, connectTimeout.Token);
                var consumer = new AsyncEventingBasicConsumer(channel);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                consumer.UnregisteredAsync += (_, _) => { cancelled.TrySetResult(); return Task.CompletedTask; };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        if (delivery.BasicProperties.ContentType != "application/json"
                            || !processor.TryProcess(delivery.Body, delivery.RoutingKey, delivery.BasicProperties.MessageId))
                        {
                            logger.LogWarning("Rejecting unsupported order event {MessageId} without requeue", delivery.BasicProperties.MessageId);
                            await channel.BasicRejectAsync(delivery.DeliveryTag, false, stoppingToken);
                            return;
                        }
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        logger.LogWarning("Order event processing failed with {FailureType}; delivery will be retried", exception.GetType().Name);
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                        if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, stoppingToken);
                    }
                };
                await channel.BasicConsumeAsync(OrderEventTopology.Queue, false, consumer, stoppingToken);
                logger.LogInformation("Listening on {Queue}", OrderEventTopology.Queue);
                failures = 0;
                while (connection.IsOpen && channel.IsOpen && !cancelled.Task.IsCompleted)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Order event worker connection failed with {FailureType}; reconnecting", exception.GetType().Name);
            }
            var delay = Math.Min(30, 1 << Math.Min(failures++, 5));
            try { await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
