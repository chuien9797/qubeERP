namespace InventoryService.Domain;

public sealed class ReservationLine
{
    public Guid ReservationId { get; init; }
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
}
