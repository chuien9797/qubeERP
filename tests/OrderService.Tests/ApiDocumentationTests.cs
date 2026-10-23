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
        var bodyOperations = document.Paths.Values.SelectMany(path => path.Operations!.Values)
            .Where(operation => operation.RequestBody is not null).ToArray();
        Assert.Equal(2, bodyOperations.Length);
        Assert.All(bodyOperations, operation => Assert.True(operation.RequestBody!.Required));
        var reconcile = Assert.Single(document.Paths["/api/orders/{id}/reconcile-cancellation"].Operations!.Values);
        Assert.Null(reconcile.RequestBody);
        Assert.DoesNotContain("413", reconcile.Responses!.Keys);
        Assert.DoesNotContain("415", reconcile.Responses.Keys);
        foreach (var (schema, field) in new[] { ("OrderLineResponse", "unitPrice"), ("OrderLineResponse", "lineTotal"), ("OrderResponse", "totalAmount") })
            Assert.Equal("decimal", document.Components!.Schemas![schema].Properties![field].Format);
        var operation = Assert.Single(document.Paths["/api/orders"].Operations!.Values, value => value.OperationId == "ListOrders");
        var parameter = Assert.Single(operation.Parameters!, value => value.Name == "status");
        Assert.False(parameter.Required);
        Assert.Equal(new[] { "Pending", "Confirmed", "Rejected", "RequiresReview", "CancellationPending", "Cancelled", "Fulfilled" },
            parameter.Schema!.Enum!.Select(value => value!.GetValue<string>()));
        Assert.False(operation.Responses!["200"].Headers?.ContainsKey("X-Malaysia-Time") == true);
        var create = Assert.Single(document.Paths["/api/orders"].Operations!.Values, value => value.OperationId == "CreateOrder");
        var uncertain = create.Responses!["202"].Content!["application/json"].Example!;
        Assert.Equal("Pending", uncertain["status"]!.GetValue<string>());
        Assert.NotNull(uncertain["statusUrl"]);
        var invalid = create.Responses["422"].Content!["application/problem+json"].Example!;
        Assert.Equal("INVALID_PRODUCT", invalid["errorCode"]!.GetValue<string>());
        Assert.NotEmpty(invalid["invalidProductIds"]!.AsArray());
        Assert.Null(invalid["orderId"]);
        var review = create.Responses["502"].Content!["application/problem+json"].Example!;
        Assert.Equal(502, review["status"]!.GetValue<int>());
        Assert.Equal("RequiresReview", review["orderStatus"]!.GetValue<string>());
        var unavailable = create.Responses["503"].Content!["application/problem+json"].Example!;
        Assert.Equal("PRODUCT_UNAVAILABLE", unavailable["errorCode"]!.GetValue<string>());
        Assert.Null(unavailable["orderId"]);
        foreach (var path in new[] { "/api/orders/{id}/cancel", "/api/orders/{id}/reconcile-cancellation" })
        {
            var cancel = Assert.Single(document.Paths[path].Operations!.Values);
            var completed = cancel.Responses!["200"].Content!["application/json"].Example!;
            Assert.Equal("Cancelled", completed["status"]!.GetValue<string>());
            Assert.Equal("Customer cancelled before shipment", completed["cancellationReason"]!.GetValue<string>());
            var pending = cancel.Responses["202"].Content!["application/json"].Example!;
            Assert.Equal("CancellationPending", pending["status"]!.GetValue<string>());
            Assert.NotNull(pending["statusUrl"]);
        }
    }
}
