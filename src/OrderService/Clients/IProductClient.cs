using Shared.Contracts.Products;

namespace OrderService.Clients;

public interface IProductClient
{
    Task<ProductLookupResponse?> LookupAsync(Guid[] productIds, CancellationToken cancellationToken);
}
