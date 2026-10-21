using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Shared.Contracts.Orders;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OrderService.OpenApi;

public sealed class OrderExamples : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(CreateOrderRequest) && schema is OpenApiSchema orderSchema)
        {
            orderSchema.Example = JsonNode.Parse("""{"items":[{"productId":"11111111-1111-1111-1111-111111111111","quantity":3},{"productId":"22222222-2222-2222-2222-222222222222","quantity":2}]}""");
        }
    }
}
