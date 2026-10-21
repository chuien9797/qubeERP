namespace InventoryService.Clients;

public interface IProductClient
{
    Task<ProductAvailability> CheckExistsAsync(Guid productId, CancellationToken cancellationToken);
}

public enum ProductAvailability
{
    Exists,
    Missing,
    Unavailable
}
