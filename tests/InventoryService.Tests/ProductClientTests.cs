using System.Net;
using InventoryService.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryService.Tests;

public sealed class ProductClientTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingProductMayBeActiveOrInactive(bool active)
    {
        var id = Guid.NewGuid();
        using var handler = new ProductStub();
        handler.AddProduct(id, active);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://product.test") };
        var client = new ProductClient(http, NullLogger<ProductClient>.Instance);
        Assert.Equal(ProductAvailability.Exists, await client.CheckExistsAsync(id, CancellationToken.None));
        Assert.Equal(1, handler.CallCount(id));
    }

    [Theory]
    [InlineData(404, "", ProductAvailability.Missing)]
    [InlineData(503, "", ProductAvailability.Unavailable)]
    [InlineData(400, "", ProductAvailability.Unavailable)]
    [InlineData(200, "not json", ProductAvailability.Unavailable)]
    [InlineData(200, "{}", ProductAvailability.Unavailable)]
    [InlineData(200, "null", ProductAvailability.Unavailable)]
    [InlineData(200, "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"sku\":\"TEST\",\"name\":\"Wrong product\"}", ProductAvailability.Unavailable)]
    public async Task MapsMissingAndUnusableResponses(int status, string body, ProductAvailability expected)
    {
        using var handler = new ProductStub();
        var id = Guid.NewGuid();
        handler.Respond(id, (HttpStatusCode)status, body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://product.test") };
        var client = new ProductClient(http, NullLogger<ProductClient>.Instance);
        Assert.Equal(expected, await client.CheckExistsAsync(id, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutOrConnectionFailureIsUnavailable(bool timeout)
    {
        using var handler = new ProductStub();
        var id = Guid.NewGuid();
        handler.Fail(id, timeout);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://product.test") };
        var client = new ProductClient(http, NullLogger<ProductClient>.Instance);
        Assert.Equal(ProductAvailability.Unavailable, await client.CheckExistsAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var handler = new ProductStub();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://product.test") };
        var client = new ProductClient(http, NullLogger<ProductClient>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckExistsAsync(Guid.NewGuid(), cancellation.Token));
    }
}
