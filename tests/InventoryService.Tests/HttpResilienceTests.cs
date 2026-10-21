using System.Net;
using InventoryService.Clients;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryService.Tests;

public sealed class HttpResilienceTests
{
    [Theory]
    [InlineData("disconnect", 3, ProductAvailability.Unavailable)]
    [InlineData("timeout", 3, ProductAvailability.Unavailable)]
    [InlineData("503", 3, ProductAvailability.Unavailable)]
    [InlineData("404", 1, ProductAvailability.Missing)]
    [InlineData("400", 1, ProductAvailability.Unavailable)]
    [InlineData("invalid-json", 1, ProductAvailability.Unavailable)]
    public async Task InitialProductLookupRetriesOnlyTransientFailures(string failure, int attempts, ProductAvailability expected)
    {
        using var products = new ProductStub();
        var id = Guid.NewGuid();
        if (failure is "disconnect" or "timeout") products.Fail(id, failure == "timeout");
        else products.Respond(id, failure == "invalid-json" ? HttpStatusCode.OK : (HttpStatusCode)int.Parse(failure), "{invalid");
        await using var factory = new InventoryApiFactory("Server=localhost;Database=Unused;Integrated Security=True", products);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(expected, await scope.ServiceProvider.GetRequiredService<IProductClient>().CheckExistsAsync(id, CancellationToken.None));
        Assert.Equal(attempts, products.CallCount(id));
    }
}
