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
    }
}
