using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Contracts.Products;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace ProductService.Tests;

[Trait("Category", "Integration")]
public sealed class ProductStatusTests(ProductApiFixture fixture) : IClassFixture<ProductApiFixture>
{
    private Task<HttpResponseMessage> SetAsync(Guid id, bool active) =>
        fixture.Client.PatchAsJsonAsync($"/api/products/{id}/status", new { isActive = active });
    private async Task<ProductResponse> ReadAsync(Guid id) =>
        (await fixture.Client.GetFromJsonAsync<ProductResponse>($"/api/products/{id}"))!;

    [Fact]
    public async Task ChangesOnlyStatusAndTimestampAndSameValueIsANoOp()
    {
        var original = await fixture.CreateProductAsync(12.34m);
        using var alreadyActive = await SetAsync(original.Id, true);
        Assert.Equal(HttpStatusCode.NoContent, alreadyActive.StatusCode);
        Assert.Equal(original, await ReadAsync(original.Id));
        using var deactivate = await SetAsync(original.Id, false);
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
        Assert.Equal("", await deactivate.Content.ReadAsStringAsync());
        var inactive = await ReadAsync(original.Id);
        Assert.False(inactive.IsActive);
        Assert.True(inactive.UpdatedAtUtc >= original.UpdatedAtUtc);
        Assert.Equal(original with { IsActive = false, UpdatedAtUtc = inactive.UpdatedAtUtc }, inactive);
        using var repeat = await SetAsync(original.Id, false);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        Assert.Equal(inactive, await ReadAsync(original.Id));
        using var reactivate = await SetAsync(original.Id, true);
        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);
        var active = await ReadAsync(original.Id);
        Assert.Equal(original with { UpdatedAtUtc = active.UpdatedAtUtc }, active);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"isActive\":null}")]
    [InlineData("{\"isActive\":\"false\"}")]
    [InlineData("{\"isActive\":0}")]
    [InlineData("{\"isActive\":")]
    public async Task MissingOrNonBooleanStatusReturns400WithoutChangingTheProduct(string json)
    {
        var original = await fixture.CreateProductAsync(2);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PatchAsync($"/api/products/{original.Id}/status", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(original, await ReadAsync(original.Id));
    }

    [Fact]
    public async Task EmptyIdIs400AndUnknownProductIs404()
    {
        using var empty = await SetAsync(Guid.Empty, false);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var missing = await SetAsync(Guid.NewGuid(), false);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var json = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal("PRODUCT_NOT_FOUND", json.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ConcurrentSameValueRequestsDoNotChurnTimestampAndCannotOverwriteOtherFields()
    {
        var original = await fixture.CreateProductAsync(4);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SetAsync(original.Id, false)));
        try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        var inactive = await ReadAsync(original.Id);
        Assert.False(inactive.IsActive);
        // An independent catalogue write must survive a status command's partial UPDATE.
        await using var database = fixture.CreateContext();
        await database.Products.Where(p => p.Id == original.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.UnitPrice, 8m));
        using var repeated = await SetAsync(original.Id, false);
        Assert.Equal(inactive with { UnitPrice = 8m }, await ReadAsync(original.Id));
        var opposite = await Task.WhenAll(SetAsync(original.Id, true), SetAsync(original.Id, false));
        try { Assert.All(opposite, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode)); }
        finally { foreach (var response in opposite) response.Dispose(); }
        // There is intentionally no arrival-order or ETag guarantee. An explicit later write wins.
        using var last = await SetAsync(original.Id, true);
        var current = await ReadAsync(original.Id);
        Assert.True(current.IsActive);
        Assert.Equal(original with { UnitPrice = 8m, UpdatedAtUtc = current.UpdatedAtUtc }, current);
    }
}

public sealed class ProductStatusDocumentationTests
{
    [Fact]
    public async Task SwaggerDocumentsExplicitBooleanNoContentAndConcurrencyLimitations()
    {
        await using var factory = new ProductApiFactory("Server=localhost;Database=Unused;Integrated Security=True;TrustServerCertificate=True");
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var operation = Assert.Single(document.Paths["/api/products/{productId}/status"].Operations!.Values);
        Assert.Equal("SetProductStatus", operation.OperationId);
        Assert.Contains("204", operation.Responses!.Keys);
        Assert.Contains("400", operation.Responses.Keys);
        Assert.Contains("404", operation.Responses.Keys);
        Assert.Contains("last committed write wins", operation.Description!);
        Assert.Contains("Pending", operation.Description!);
        var schema = document.Components!.Schemas!["UpdateProductStatusRequest"];
        Assert.Contains("isActive", schema.Required!);
        Assert.False(schema.Example!["isActive"]!.GetValue<bool>());
    }
}
