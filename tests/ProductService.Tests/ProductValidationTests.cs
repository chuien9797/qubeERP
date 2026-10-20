using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ProductService.Tests;

public sealed class ProductValidationTests
{
    public static IEnumerable<object[]> InvalidProducts()
    {
        yield return ["{}"];
        yield return ["null"];
        yield return ["{invalid"];
        yield return ["""{"sku":" ","name":"Mouse","unitPrice":1}"""];
        yield return ["""{"sku":"MOUSE","name":" ","unitPrice":1}"""];
        yield return ["""{"sku":"MOUSE","name":"Mouse"}"""];
        yield return ["""{"sku":"MOUSE","name":"Mouse","unitPrice":-1}"""];
        yield return ["""{"sku":"MOUSE","name":"Mouse","unitPrice":1.001}"""];
        yield return ["""{"sku":"MOUSE","name":"Mouse","unitPrice":10000000000000000}"""];
        yield return [JsonSerializer.Serialize(new { sku = new string('s', 51), name = "Mouse", unitPrice = 1 })];
        yield return [JsonSerializer.Serialize(new { sku = "MOUSE", name = new string('n', 201), unitPrice = 1 })];
    }

    public static IEnumerable<object[]> InvalidLookups()
    {
        yield return ["{}"];
        yield return ["""{"productIds":[]}"""];
        yield return ["""{"productIds":["not-a-guid"]}"""];
        yield return [JsonSerializer.Serialize(new { productIds = new[] { Guid.Empty } })];
        var id = Guid.NewGuid();
        yield return [JsonSerializer.Serialize(new { productIds = new[] { id, id } })];
        yield return [JsonSerializer.Serialize(new { productIds = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()) })];
    }

    [Theory]
    [MemberData(nameof(InvalidProducts))]
    public Task InvalidProductIsRejectedBeforeDatabaseAccess(string body) => AssertInvalid("/api/products", body);

    [Theory]
    [MemberData(nameof(InvalidLookups))]
    public Task InvalidLookupIsRejectedBeforeDatabaseAccess(string body) => AssertInvalid("/api/products/lookup", body);

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?offset=-1")]
    public async Task InvalidListBoundsReturnValidationProblem(string query)
    {
        await using var factory = new ProductApiFactory("Server=localhost;Database=Unused;Integrated Security=True");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/products" + query);
        await AssertProblem(response);
    }

    private static async Task AssertInvalid(string route, string body)
    {
        await using var factory = new ProductApiFactory("Server=localhost;Database=Unused;Integrated Security=True");
        using var client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(route, content);
        await AssertProblem(response);
    }

    private static async Task AssertProblem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_REQUEST", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").EnumerateObject().Any());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
    }
}
