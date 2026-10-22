namespace OrderService.Domain;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public string EventType { get; init; } = "";
    public int EventVersion { get; init; }
    public Guid AggregateId { get; init; }
    public string PayloadJson { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
