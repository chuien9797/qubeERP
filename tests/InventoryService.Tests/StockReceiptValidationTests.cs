using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shared.Contracts.Inventory;
using Xunit;

namespace InventoryService.Tests;

public sealed class StockReceiptValidationTests
{
    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return ["{}"];
        yield return ["null"];
        yield return ["{invalid"];
        foreach (var quantity in new object?[] { null, 0, -1, 1.5, 2147483648L })
            yield return [JsonSerializer.Serialize(new { operationId = Guid.NewGuid(), quantity, reason = "Delivery" })];
        foreach (var operationId in new object?[] { null, Guid.Empty, "bad-id" })
            yield return [JsonSerializer.Serialize(new { operationId, quantity = 5, reason = "Delivery" })];
        foreach (var reason in new[] { null, "", " ", new string('x', 301) })
            yield return [JsonSerializer.Serialize(new { operationId = Guid.NewGuid(), quantity = 5, reason })];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidRequestsFailWithoutDatabaseAccess(string body)
    {
        using var products = new ProductStub();
        await using var factory = new InventoryApiFactory("Server=localhost;Database=Unused;Integrated Security=True", products);
        using var client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/api/inventory/{Guid.NewGuid()}/receipts", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_REQUEST", json.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task EmptyProductIdFailsWithoutDatabaseAccess()
    {
        using var products = new ProductStub();
        await using var factory = new InventoryApiFactory("Server=localhost;Database=Unused;Integrated Security=True", products);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/inventory/{Guid.Empty}/receipts",
            new ReceiveStockRequest { OperationId = Guid.NewGuid(), Quantity = 5, Reason = "Delivery" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
