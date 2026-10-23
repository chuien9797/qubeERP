using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace ProductService.Tests;

public sealed class ApiDocumentationTests
{
    [Fact]
    public async Task ProductContractsRequireBodiesAndUseDecimalMoney()
    {
        await using var factory = new ProductApiFactory(
            "Server=localhost;Database=Unused;Integrated Security=True;TrustServerCertificate=True");
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var bodyOperations = document.Paths.Values.SelectMany(path => path.Operations!.Values)
            .Where(operation => operation.RequestBody is not null).ToArray();
        Assert.Equal(3, bodyOperations.Length);
        Assert.All(bodyOperations, operation => Assert.True(operation.RequestBody!.Required));
        foreach (var schema in new[] { "CreateProductRequest", "ProductResponse" })
            Assert.Equal("decimal", document.Components!.Schemas![schema].Properties!["unitPrice"].Format);
    }
}
