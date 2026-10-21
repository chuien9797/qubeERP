namespace InventoryService.Domain;

public sealed class StockMovement
{
    public long Id { get; private set; }
    public Guid OperationId { get; init; }
    public Guid ProductId { get; init; }
    public string MovementType { get; init; } = string.Empty;
    public int OnHandDelta { get; init; }
    public int ReservedDelta { get; init; }
    public int OnHandAfter { get; init; }
    public int ReservedAfter { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}
