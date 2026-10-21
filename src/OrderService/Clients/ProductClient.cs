using System.Net;
using System.Text.Json;
using Shared.Contracts.Products;

namespace OrderService.Clients;

public sealed class ProductClient(HttpClient httpClient, ILogger<ProductClient> logger) : IProductClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    public async Task<ProductLookupResponse?> LookupAsync(Guid[] productIds, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("/api/products/lookup",
                new ProductLookupRequest { ProductIds = productIds }, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Product batch lookup returned {StatusCode}", response.StatusCode);
                return null;
            }

            var lookup = await response.Content.ReadFromJsonAsync<ProductLookupResponse>(JsonOptions, cancellationToken);
            if (lookup?.Products is null || lookup.MissingProductIds is null
                || lookup.Products.Any(product => product is null || product.Id == Guid.Empty
                    || string.IsNullOrWhiteSpace(product.Sku) || product.Sku.Length > 50
                    || string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 200
                    || product.UnitPrice < 0 || product.UnitPrice > 9999999999999999.99m
                    || decimal.Round(product.UnitPrice, 2) != product.UnitPrice))
            {
                logger.LogWarning("Product batch lookup returned an invalid product snapshot");
                return null;
            }

            var returnedIds = lookup.Products.Select(product => product.Id).Concat(lookup.MissingProductIds).ToArray();
            if (returnedIds.Length != productIds.Length || returnedIds.Distinct().Count() != returnedIds.Length
                || !returnedIds.ToHashSet().SetEquals(productIds))
            {
                logger.LogWarning("Product batch lookup returned IDs that do not match the request");
                return null;
            }

            return lookup;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Product batch lookup failed");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Product batch lookup timed out");
            return null;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Product batch lookup returned invalid JSON");
            return null;
        }
    }
}
