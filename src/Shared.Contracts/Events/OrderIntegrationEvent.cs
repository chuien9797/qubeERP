namespace Shared.Contracts.Events;

public sealed record OrderIntegrationEvent(Guid EventId, string EventType, int Version, Guid OrderId,
    string Status, DateTimeOffset OccurredAtUtc)
{
    public bool IsSupported() => EventId != Guid.Empty && OrderId != Guid.Empty && Version == 1
        && OccurredAtUtc != default && OccurredAtUtc.Offset == TimeSpan.Zero
        && (EventType == "OrderConfirmed" && Status == "Confirmed"
            || EventType == "OrderRejected" && Status == "Rejected");
}
