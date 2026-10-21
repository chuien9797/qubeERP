using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OrderService.Tests;

public sealed class OrderValidationTests
{
    private const string Item = """{"productId":"11111111-1111-1111-1111-111111111111","quantity":1}""";

    public static IEnumerable<object[]> InvalidRequests()
    {
        foreach (var body in new[] { "{}", "null", "{invalid", "{\"items\":null}", "{\"items\":[]}", "{\"items\":[null]}" })
        {
            yield return [body, "valid-key"];
        }
        yield return [Body(Item + "," + Item), "valid-key"];
        foreach (var id in new[] { "00000000-0000-0000-0000-000000000000", "invalid" })
        {
            yield return [Body("{\"productId\":\"" + id + "\",\"quantity\":1}"), "valid-key"];
        }
        yield return [Body("{\"quantity\":1}"), "valid-key"];
        foreach (var quantity in new[] { "0", "-1", "10001", "2147483648", "1.5", "null" })
        {
            yield return [Body("{\"productId\":\"11111111-1111-1111-1111-111111111111\",\"quantity\":" + quantity + "}"), "valid-key"];
        }
        yield return [Body("{\"productId\":\"11111111-1111-1111-1111-111111111111\"}"), "valid-key"];
        yield return [JsonSerializer.Serialize(new { items = Enumerable.Range(0, 51).Select(_ => new { productId = Guid.NewGuid(), quantity = 1 }) }), "valid-key"];
        foreach (var key in new string?[] { null, "", "   ", new('x', 129) })
        {
            yield return [Body(Item), key!];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidRequestsFailBeforeDatabaseOrHttp(string body, string? key)
    {
        using var dependencies = new DependencyStub();
        await using var factory = new OrderApiFactory("Server=localhost;Database=Unused;Integrated Security=True", dependencies);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
        Assert.Equal(0, dependencies.ProductCalls);
        Assert.Equal(0, dependencies.InventoryCalls);
    }

    private static string Body(string items) => "{\"items\":[" + items + "]}";
}
