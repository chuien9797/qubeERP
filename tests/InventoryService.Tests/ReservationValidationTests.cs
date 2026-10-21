using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InventoryService.Tests;

public sealed class ReservationValidationTests
{
    private const string ReservationId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Item = """{"productId":"11111111-1111-1111-1111-111111111111","quantity":1}""";

    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return ["{}"];
        yield return ["null"];
        yield return ["{invalid"];
        yield return ["""{"items":[{"productId":"11111111-1111-1111-1111-111111111111","quantity":1}]}"""];
        yield return [Body("[" + Item + "]", Guid.Empty.ToString())];
        yield return [Body("[" + Item + "]", "invalid-id")];
        yield return [Body("null")];
        yield return [Body("[]")];
        yield return [Body("[null]")];
        yield return [Body("[" + Item + "," + Item + "]")];
        yield return [Body("""[{"productId":"00000000-0000-0000-0000-000000000000","quantity":1}]""")];
        yield return [Body("""[{"productId":"invalid-id","quantity":1}]""")];
        yield return [Body("""[{"quantity":1}]""")];
        foreach (var quantity in new[] { "0", "-1", "10001", "2147483648", "1.5", "null" })
        {
            yield return [Body("[{\"productId\":\"11111111-1111-1111-1111-111111111111\",\"quantity\":" + quantity + "}]")];
        }

        yield return [Body("""[{"productId":"11111111-1111-1111-1111-111111111111"}]""")];
        var items = Enumerable.Range(0, 51).Select(_ => new { productId = Guid.NewGuid(), quantity = 1 });
        yield return [JsonSerializer.Serialize(new { reservationId = ReservationId, items })];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidReservationHasNoDatabaseOrProductSideEffects(string body)
    {
        using var products = new ProductStub();
        await using var factory = new InventoryApiFactory("Server=localhost;Database=Unused;Integrated Security=True", products);
        using var client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/inventory/reservations", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_RESERVATION", document.RootElement.GetProperty("errorCode").GetString());
        Assert.True(document.RootElement.GetProperty("errors").EnumerateObject().Any());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
    }

    private static string Body(string items, string id = ReservationId)
    {
        return "{\"reservationId\":\"" + id + "\",\"items\":" + items + "}";
    }
}
