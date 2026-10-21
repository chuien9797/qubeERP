namespace InventoryService.Domain;

public sealed class Reservation
{
    public Guid Id { get; init; }
    public string RequestFingerprint { get; init; } = string.Empty;
    public string Status { get; set; } = "Processing";
    public string? FailureCode { get; set; }
    public string? FailureDetailsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
