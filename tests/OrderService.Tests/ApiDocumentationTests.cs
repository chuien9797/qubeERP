using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace OrderService.Tests;

public sealed class ApiDocumentationTests
{
    [Fact]
    public async Task OrderStatusFilterIsAnOptionalStringDropdownWithExistingWireValues()
    {
        using var dependencies = new DependencyStub();
        await using var factory = new OrderApiFactory(
            "Server=localhost;Database=Unused;Integrated Security=True;TrustServerCertificate=True", dependencies);
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var operation = Assert.Single(document.Paths["/api/orders"].Operations!.Values, value => value.OperationId == "ListOrders");
        var parameter = Assert.Single(operation.Parameters!, value => value.Name == "status");
        Assert.False(parameter.Required);
        Assert.Equal(new[] { "Pending", "Confirmed", "Rejected", "RequiresReview", "CancellationPending", "Cancelled", "Fulfilled" },
            parameter.Schema!.Enum!.Select(value => value!.GetValue<string>()));
        Assert.Contains("X-Malaysia-Time", operation.Responses!["200"].Headers!.Keys);
        Assert.Contains("GMT", operation.Responses["200"].Headers!["X-Malaysia-Time"].Description!);
    }
}
