using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderService.Data;
using QubeERP.Messaging;
using Shared.Contracts.Events;

namespace OrderService.Messaging;

public sealed class OutboxDispatcher(IDbContextFactory<OrderServiceContext> contexts, IOrderEventPublisher publisher,
    IOptions<OutboxSettings> options, ILogger<OutboxDispatcher> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var messages = await database.OutboxMessages.Where(value => value.PublishedAtUtc == null && value.NextAttemptAtUtc <= now)
            .OrderBy(value => value.NextAttemptAtUtc).ThenBy(value => value.CreatedAtUtc)
            .Take(options.Value.BatchSize).ToArrayAsync(cancellationToken);
        foreach (var stored in messages)
        {
            try
            {
                var message = JsonSerializer.Deserialize<OrderIntegrationEvent>(stored.PayloadJson, JsonOptions);
                if (message is null || !message.IsSupported() || message.EventId != stored.Id
                    || message.OrderId != stored.AggregateId || message.EventType != stored.EventType || message.Version != stored.EventVersion)
                    throw new InvalidOperationException("Outbox payload does not match its envelope.");
                await publisher.PublishAsync(message, cancellationToken);
                stored.PublishedAtUtc = DateTimeOffset.UtcNow;
                stored.LastError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                stored.LastError = exception.GetType().Name;
                var seconds = Math.Min(options.Value.MaxBackoffSeconds, Math.Pow(2, Math.Min(stored.AttemptCount, 15) + 1));
                stored.NextAttemptAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
                logger.LogWarning("Outbox event {EventId} publication failed with {FailureType}; retry scheduled", stored.Id, stored.LastError);
            }
            if (stored.AttemptCount < int.MaxValue) stored.AttemptCount++;
            await database.SaveChangesAsync(cancellationToken);
            if (stored.PublishedAtUtc is not null)
                logger.LogInformation("Published {EventType}: EventId={EventId}, OrderId={OrderId}", stored.EventType, stored.Id, stored.AggregateId);
        }
        return messages.Length;
    }
}
