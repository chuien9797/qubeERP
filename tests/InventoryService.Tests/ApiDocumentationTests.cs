using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace InventoryService.Tests;

public sealed class ApiDocumentationTests
{
    [Fact]
    public async Task ReservationGetDocumentsHistoricalItems()
    {
        using var products = new ProductStub();
        await using var factory = new InventoryApiFactory(
            "Server=localhost;Database=Unused;Integrated Security=True;TrustServerCertificate=True", products);
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var bodyOperations = document.Paths.Values.SelectMany(path => path.Operations!.Values)
            .Where(operation => operation.RequestBody is not null).ToArray();
        Assert.Equal(4, bodyOperations.Length);
        Assert.All(bodyOperations, operation => Assert.True(operation.RequestBody!.Required));
        var stock = document.Components!.Schemas!["InventoryResponse"];
        Assert.Contains("version", stock.Properties!.Keys);
        Assert.Equal(8, Convert.FromBase64String(stock.Example!["version"]!.GetValue<string>()).Length);
        var count = document.Components.Schemas["SetStockRequest"];
        Assert.Contains("expectedVersion", count.Properties!.Keys);
        var put = Assert.Single(document.Paths["/api/inventory/{productId}"].Operations!.Values, value => value.OperationId == "SetPhysicalStock");
        Assert.Contains("STOCK_VERSION_CONFLICT", put.Responses!["409"].Description!);
        var release = Assert.Single(document.Paths["/api/inventory/reservations/{reservationId}/release"].Operations!.Values);
        var releaseExample = release.Responses!["200"].Content!["application/json"].Example!;
        Assert.Equal("Released", releaseExample["status"]!.GetValue<string>());
        var requestId = document.Components.Schemas["CreateReservationRequest"].Example!["reservationId"]!.GetValue<string>();
        Assert.Equal(requestId, releaseExample["reservationId"]!.GetValue<string>());
        Assert.Equal(requestId, document.Components.Schemas["ReservationResponse"].Example!["reservationId"]!.GetValue<string>());
        var operation = Assert.Single(document.Paths["/api/inventory/reservations/{reservationId}"].Operations!.Values);
        Assert.Equal("GetReservation", operation.OperationId);
        var schema = document.Components!.Schemas!["ReservationDetailsResponse"];
        Assert.Contains("items", schema.Properties!.Keys);
        Assert.Equal(2, schema.Example!["items"]!.AsArray().Count);
        var item = document.Components.Schemas["ReservationItemResponse"];
        Assert.Equal(new[] { "productId", "quantity" }, item.Properties!.Keys.OrderBy(name => name));
        Assert.Contains("historical", operation.Responses!["200"].Description!, StringComparison.OrdinalIgnoreCase);
        Assert.False(operation.Responses["200"].Headers?.ContainsKey("X-Malaysia-Time") == true);
        var examples = operation.Responses["200"].Content!["application/json"].Examples!;
        Assert.Equal(new[] { "Consumed", "Rejected", "Released", "Reserved" }, examples.Keys.OrderBy(name => name));
        foreach (var (status, example) in examples)
        {
            Assert.Equal(status, example.Value!["status"]!.GetValue<string>());
            Assert.Equal(3, example.Value["items"]![0]!["quantity"]!.GetValue<int>());
            Assert.Equal(2, example.Value["items"]![1]!["quantity"]!.GetValue<int>());
            Assert.Equal(status == "Rejected" ? 1 : 0, example.Value["shortfalls"]!.AsArray().Count);
            Assert.Equal(status == "Rejected" ? "INSUFFICIENT_STOCK" : null, example.Value["failureCode"]?.GetValue<string>());
        }
    }
}
