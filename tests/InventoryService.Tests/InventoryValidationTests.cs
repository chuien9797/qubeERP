using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InventoryService.Tests;

public sealed class InventoryValidationTests
{
    public static IEnumerable<object[]> InvalidStockRequests()
    {
        yield return ["{}"];
        yield return ["null"];
        yield return ["{invalid"];
        yield return ["""{"onHand":-1,"reason":"Count"}"""];
        yield return ["""{"onHand":2147483648,"reason":"Count"}"""];
        yield return ["""{"onHand":1.5,"reason":"Count"}"""];
        yield return ["""{"reason":"Count"}"""];
        yield return ["""{"onHand":10}"""];
        yield return ["""{"onHand":10,"reason":" "}"""];
        yield return [JsonSerializer.Serialize(new { onHand = 10, reason = new string('x', 301) })];
    }

    [Theory]
    [MemberData(nameof(InvalidStockRequests))]
    public async Task InvalidPutFailsBeforeDatabaseOrProductAccess(string body)
    {
        using var products = new ProductStub();
        await using var factory = new InventoryApiFactory("Server=localhost;Database=Unused;Integrated Security=True", products);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync($"/api/inventory/{id}", content);
        await AssertInvalid(response);
        Assert.Equal(0, products.CallCount(id));
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?offset=-1")]
    public async Task InvalidLedgerBoundsFailBeforeDatabaseAccess(string query)
    {
        using var products = new ProductStub();
        await using var factory = new InventoryApiFactory("Server=localhost;Database=Unused;Integrated Security=True", products);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/inventory/{Guid.NewGuid()}/movements{query}");
        await AssertInvalid(response);
    }

    [Fact]
    public async Task EmptyProductIdIsInvalid()
    {
        using var products = new ProductStub();
        await using var factory = new InventoryApiFactory("Server=localhost;Database=Unused;Integrated Security=True", products);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/inventory/{Guid.Empty}");
        await AssertInvalid(response);
    }

    private static async Task AssertInvalid(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_REQUEST", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
    }
}
