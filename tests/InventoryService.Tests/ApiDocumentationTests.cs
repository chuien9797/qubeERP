using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace InventoryService.Tests;

public sealed class ApiDocumentationTests
{
    [Fact]
    public async Task ReservationGetDocumentsHistoricalItemsAndMalaysiaResponseTime()
    {
        using var products = new ProductStub();
        await using var factory = new InventoryApiFactory(
            "Server=localhost;Database=Unused;Integrated Security=True;TrustServerCertificate=True", products);
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var operation = Assert.Single(document.Paths["/api/inventory/reservations/{reservationId}"].Operations!.Values);
        Assert.Equal("GetReservation", operation.OperationId);
        var schema = document.Components!.Schemas!["ReservationDetailsResponse"];
        Assert.Contains("items", schema.Properties!.Keys);
        Assert.Equal(2, schema.Example!["items"]!.AsArray().Count);
        var item = document.Components.Schemas["ReservationItemResponse"];
        Assert.Equal(new[] { "productId", "quantity" }, item.Properties!.Keys.OrderBy(name => name));
        Assert.Contains("historical", operation.Responses!["200"].Description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Malaysia-Time", operation.Responses["200"].Headers!.Keys);
    }
}
