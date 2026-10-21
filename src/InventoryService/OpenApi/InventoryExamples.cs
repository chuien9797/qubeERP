using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Shared.Contracts.Inventory;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InventoryService.OpenApi;

public sealed class InventoryExamples : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(SetStockRequest) && schema is OpenApiSchema stockSchema)
        {
            stockSchema.Example = JsonNode.Parse("""{"onHand":10,"reason":"Initial stocktake"}""");
        }

        if (context.Type == typeof(CreateReservationRequest) && schema is OpenApiSchema reservationSchema)
        {
            reservationSchema.Example = JsonNode.Parse("""{"reservationId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","items":[{"productId":"11111111-1111-1111-1111-111111111111","quantity":3},{"productId":"22222222-2222-2222-2222-222222222222","quantity":2}]}""");
        }
    }
}
