using System.Net;
using System.Text.Json;
using Shared.Contracts.Products;

namespace InventoryService.Clients;

public sealed class ProductClient(HttpClient httpClient, ILogger<ProductClient> logger) : IProductClient
{
    public async Task<ProductAvailability> CheckExistsAsync(Guid productId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync($"/api/products/{productId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ProductAvailability.Missing;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Product lookup for {ProductId} returned {StatusCode}", productId, response.StatusCode);
                return ProductAvailability.Unavailable;
            }

            var product = await response.Content.ReadFromJsonAsync<ProductResponse>(cancellationToken);
            if (product?.Id == productId && !string.IsNullOrWhiteSpace(product.Sku)
                && !string.IsNullOrWhiteSpace(product.Name))
            {
                return ProductAvailability.Exists;
            }

            logger.LogWarning("Product lookup for {ProductId} returned an invalid snapshot", productId);
            return ProductAvailability.Unavailable;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Product lookup failed for {ProductId}", productId);
            return ProductAvailability.Unavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Product lookup timed out for {ProductId}", productId);
            return ProductAvailability.Unavailable;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Product lookup returned invalid JSON for {ProductId}", productId);
            return ProductAvailability.Unavailable;
        }
    }
}
