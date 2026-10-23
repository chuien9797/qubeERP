namespace InventoryService.Domain;

public sealed class StockItem
{
    public Guid ProductId { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] Version { get; set; } = [];
}
