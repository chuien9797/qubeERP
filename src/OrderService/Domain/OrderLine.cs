namespace OrderService.Domain;

public sealed class OrderLine
{
    public Guid OrderId { get; init; }
    public Guid ProductId { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public decimal UnitPrice { get; init; }
    public int Quantity { get; init; }
    public decimal LineTotal { get; init; }
}
