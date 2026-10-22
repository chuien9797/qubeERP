using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ProductService.Data;
using Shared.Contracts.Products;
using Testcontainers.MsSql;
using Xunit;

namespace ProductService.Tests;

[Trait("Category", "Integration")]
public sealed class ProductApiTests(ProductApiFixture fixture) : IClassFixture<ProductApiFixture>
{
    [Fact]
    public async Task CreateNormalizesSkuAndNameAndIsRetrievable()
    {
        var sku = "mouse-" + Guid.NewGuid().ToString("N");
        using var response = await fixture.Client.PostAsJsonAsync("/api/products", new CreateProductRequest
        {
            Sku = " " + sku + " ", Name = " Wireless Mouse ", Description = "2.4 GHz", UnitPrice = 59.90m
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
        Assert.NotEqual(Guid.Empty, product.Id);
        Assert.Equal(sku.ToUpperInvariant(), product.Sku);
        Assert.Equal("Wireless Mouse", product.Name);
        Assert.Equal("2.4 GHz", product.Description);
        Assert.Equal(59.90m, product.UnitPrice);
        Assert.True(product.IsActive);
        Assert.Equal(TimeSpan.Zero, product.CreatedAtUtc.Offset);
        Assert.True(product.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.True(product.UpdatedAtUtc >= product.CreatedAtUtc);
        Assert.Equal($"/api/products/{product.Id}", response.Headers.Location!.AbsolutePath);
        var retrieved = await fixture.Client.GetFromJsonAsync<ProductResponse>(response.Headers.Location);
        Assert.Equal(product, retrieved);
    }

    [Fact]
    public async Task ConcurrentCanonicalDuplicateHasOneWinner()
    {
        var sku = "race-" + Guid.NewGuid().ToString("N");
        var requests = new[] { sku, " " + sku.ToUpperInvariant() + " " };
        var responses = await Task.WhenAll(requests.Select(value => fixture.Client.PostAsJsonAsync("/api/products",
            new CreateProductRequest { Sku = value, Name = "Mouse", UnitPrice = 1 })));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            var duplicate = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
            Assert.Equal("DUPLICATE_SKU", problem.RootElement.GetProperty("errorCode").GetString());
            await using var database = fixture.CreateContext();
            Assert.Equal(1, await database.Products.CountAsync(product => product.Sku == sku.ToUpperInvariant()));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task LookupReturnsSnapshotsInactiveProductsAndMissingIds()
    {
        var product = await fixture.CreateProductAsync(12.34m);
        await using (var database = fixture.CreateContext())
            await database.Products.Where(value => value.Id == product.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.IsActive, false));
        var missing = Guid.NewGuid();
        using var response = await fixture.Client.PostAsJsonAsync("/api/products/lookup",
            new ProductLookupRequest { ProductIds = [missing, product.Id] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lookup = (await response.Content.ReadFromJsonAsync<ProductLookupResponse>())!;
        Assert.Equal(new[] { missing }, lookup.MissingProductIds);
        var found = Assert.Single(lookup.Products);
        Assert.Equal(product with { IsActive = false }, found);
        var retrieved = await fixture.Client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}");
        Assert.Equal(found, retrieved);
    }

    [Fact]
    public async Task LookupAcceptsFiftyDistinctIds()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        using var response = await fixture.Client.PostAsJsonAsync("/api/products/lookup", new ProductLookupRequest { ProductIds = ids });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lookup = (await response.Content.ReadFromJsonAsync<ProductLookupResponse>())!;
        Assert.Empty(lookup.Products);
        Assert.Equal(ids, lookup.MissingProductIds);
    }

    [Fact]
    public async Task MissingProductReturnsNotFound()
    {
        using var response = await fixture.Client.GetAsync($"/api/products/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PRODUCT_NOT_FOUND", problem.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ListHonorsBoundsAndStableOrder()
    {
        await fixture.CreateProductAsync(0);
        await fixture.CreateProductAsync(9999999999999999.99m);
        var first = await fixture.Client.GetFromJsonAsync<ProductResponse[]>("/api/products?limit=1");
        var second = await fixture.Client.GetFromJsonAsync<ProductResponse[]>("/api/products?offset=1&limit=1");
        var pair = await fixture.Client.GetFromJsonAsync<ProductResponse[]>("/api/products?limit=2");
        Assert.Equal(Assert.Single(first!), pair![0]);
        Assert.Equal(Assert.Single(second!), pair[1]);
        Assert.NotEqual(first![0].Id, second![0].Id);
    }

    [Fact]
    public async Task SqlRejectsNegativePriceAndDuplicateSkuWithoutApiValidation()
    {
        var product = await fixture.CreateProductAsync(0);
        await using var database = fixture.CreateContext();
        var negative = await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Products (Sku, Name, UnitPrice) VALUES ({Guid.NewGuid().ToString()}, {"Invalid"}, {-1m})"));
        Assert.Equal(547, negative.Number);
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Products (Sku, Name, UnitPrice) VALUES ({product.Sku}, {"Duplicate"}, {0m})"));
        Assert.True(duplicate.Number is 2601 or 2627);
    }
}

public sealed class ProductApiFixture : IAsyncLifetime
{
    private readonly MsSqlContainer sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718").Build();
    private ProductApiFactory? factory;
    public HttpClient Client { get; private set; } = null!;

    public ProductServiceContext CreateContext()
    {
        var connection = new SqlConnectionStringBuilder(sql.GetConnectionString()) { InitialCatalog = "ProductDb" };
        return new ProductServiceContext(new DbContextOptionsBuilder<ProductServiceContext>()
            .UseSqlServer(connection.ConnectionString).Options);
    }

    public async Task InitializeAsync()
    {
        await sql.StartAsync();
        await using var database = CreateContext();
        await database.Database.MigrateAsync();
        factory = new ProductApiFactory(database.Database.GetConnectionString()!);
        Client = factory.CreateClient();
    }

    public async Task<ProductResponse> CreateProductAsync(decimal price)
    {
        using var response = await Client.PostAsJsonAsync("/api/products", new CreateProductRequest
        {
            Sku = "SKU-" + Guid.NewGuid().ToString("N"), Name = "Test Product", UnitPrice = price
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (factory is not null)
            await factory.DisposeAsync();
        await sql.DisposeAsync();
    }
}
