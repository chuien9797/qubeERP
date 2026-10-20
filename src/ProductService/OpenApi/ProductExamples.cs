using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Shared.Contracts.Products;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ProductService.OpenApi;

public sealed class ProductExamples : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema mutableSchema)
            return;
        if (context.Type == typeof(CreateProductRequest))
            mutableSchema.Example = JsonNode.Parse("""{"sku":" mouse-01 ","name":"Wireless Mouse","description":"2.4 GHz","unitPrice":59.90}""");
        if (context.Type == typeof(ProductLookupRequest))
            mutableSchema.Example = JsonNode.Parse("""{"productIds":["11111111-1111-1111-1111-111111111111","22222222-2222-2222-2222-222222222222"]}""");
    }
}
