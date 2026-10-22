namespace OrderService.Domain;

public sealed class Order
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string IdempotencyKey { get; init; }
    public required string RequestFingerprint { get; init; }
    public string Status { get; set; } = "Pending";
    public decimal TotalAmount { get; init; }
    public string? FailureCode { get; set; }
    public string? FailureDetailsJson { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public List<OrderLine> Lines { get; init; } = [];
}
