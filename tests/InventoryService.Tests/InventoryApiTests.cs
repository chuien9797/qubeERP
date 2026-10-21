using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryService.Data;
using InventoryService.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Inventory;
using Testcontainers.MsSql;
using Xunit;

namespace InventoryService.Tests;

[Trait("Category", "Integration")]
[Collection("Inventory SQL")]
public sealed class InventoryApiTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    [Fact]
    public async Task InitialStockCreatesBalanceAndOneMovement()
    {
        var id = fixture.AddProduct();
        var balance = await fixture.SetStockAsync(id, 10, " Initial stocktake ");
        Assert.Equal(new InventoryResponse(id, 10, 0, 10), balance);
        Assert.Equal(balance, await fixture.Client.GetFromJsonAsync<InventoryResponse>($"/api/inventory/{id}"));
        var movement = Assert.Single(await fixture.GetMovementsAsync(id));
        Assert.Equal("InitialStock", movement.MovementType);
        Assert.Equal(10, movement.OnHandDelta);
        Assert.Equal(0, movement.ReservedDelta);
        Assert.Equal(10, movement.OnHandAfter);
        Assert.Equal(0, movement.ReservedAfter);
        Assert.Equal("Initial stocktake", movement.Reason);
        Assert.NotEqual(Guid.Empty, movement.OperationId);
        Assert.Equal(TimeSpan.Zero, movement.CreatedAtUtc.Offset);
        Assert.Equal(1, fixture.Products.CallCount(id));
    }

    [Fact]
    public async Task ZeroInitializationCreatesNoMovement()
    {
        var id = fixture.AddProduct();
        Assert.Equal(new InventoryResponse(id, 0, 0, 0), await fixture.SetStockAsync(id, 0));
        Assert.Empty(await fixture.GetMovementsAsync(id));
        await fixture.SetStockAsync(id, 5);
        var movement = Assert.Single(await fixture.GetMovementsAsync(id));
        Assert.Equal("StockSet", movement.MovementType);
        Assert.Equal(5, movement.OnHandDelta);
    }

    [Fact]
    public async Task RepeatedTargetDoesNotChangeTimestampOrAddLedgerRows()
    {
        var id = fixture.AddProduct();
        await fixture.SetStockAsync(id, 10);
        DateTimeOffset timestamp;
        await using (var database = fixture.CreateContext())
        {
            timestamp = (await database.StockItems.SingleAsync(item => item.ProductId == id)).UpdatedAtUtc;
        }

        await fixture.SetStockAsync(id, 10, "Repeated stocktake");
        Assert.Single(await fixture.GetMovementsAsync(id));
        await using var after = fixture.CreateContext();
        Assert.Equal(timestamp, (await after.StockItems.SingleAsync(item => item.ProductId == id)).UpdatedAtUtc);
        Assert.Equal(1, fixture.Products.CallCount(id));
    }

    [Fact]
    public async Task ChangedTargetsRecordExactDeltasAndLedgerPagination()
    {
        var id = fixture.AddProduct();
        await fixture.SetStockAsync(id, 10);
        await fixture.SetStockAsync(id, 15, "Recount");
        await fixture.SetStockAsync(id, 4, "Damaged stock count");
        var movements = await fixture.GetMovementsAsync(id);
        Assert.Equal(new[] { 10, 5, -11 }, movements.Select(movement => movement.OnHandDelta));
        Assert.Equal(new[] { "InitialStock", "StockSet", "StockSet" }, movements.Select(movement => movement.MovementType));
        Assert.Equal(new[] { 10, 15, 4 }, movements.Select(movement => movement.OnHandAfter));
        Assert.Equal(3, movements.Select(movement => movement.OperationId).Distinct().Count());
        Assert.True(movements[0].Id < movements[1].Id && movements[1].Id < movements[2].Id);
        var page = await fixture.Client.GetFromJsonAsync<StockMovementResponse[]>($"/api/inventory/{id}/movements?offset=1&limit=1");
        Assert.Equal(movements[1], Assert.Single(page!));
    }

    [Theory]
    [InlineData(404, 422, "PRODUCT_NOT_FOUND")]
    [InlineData(503, 503, "PRODUCT_UNAVAILABLE")]
    [InlineData(200, 503, "PRODUCT_UNAVAILABLE")]
    public async Task MissingOrUnavailableProductCannotInitializeStock(int productStatus, int status, string code)
    {
        var id = Guid.NewGuid();
        fixture.Products.Respond(id, (HttpStatusCode)productStatus, "{}");
        using var response = await fixture.PutAsync(id, 10);
        await AssertProblem(response, status, code);
        await using var database = fixture.CreateContext();
        Assert.False(await database.StockItems.AnyAsync(item => item.ProductId == id));
        Assert.False(await database.StockMovements.AnyAsync(item => item.ProductId == id));
    }

    [Fact]
    public async Task ExistingStockDoesNotDependOnProductService()
    {
        var id = fixture.AddProduct();
        await fixture.SetStockAsync(id, 10);
        fixture.Products.Respond(id, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(new InventoryResponse(id, 8, 0, 8), await fixture.SetStockAsync(id, 8));
        Assert.Equal(1, fixture.Products.CallCount(id));
    }

    [Fact]
    public async Task MissingInventoryReadsReturnNotFoundWithoutProductCalls()
    {
        var id = Guid.NewGuid();
        using var balance = await fixture.Client.GetAsync($"/api/inventory/{id}");
        using var movements = await fixture.Client.GetAsync($"/api/inventory/{id}/movements");
        await AssertProblem(balance, 404, "STOCK_NOT_FOUND");
        await AssertProblem(movements, 404, "STOCK_NOT_FOUND");
        Assert.Equal(0, fixture.Products.CallCount(id));
    }

    [Fact]
    public async Task StockCannotBeSetBelowReservedAndValidUpdatesPreserveReserved()
    {
        var id = fixture.AddProduct();
        await fixture.SetStockAsync(id, 10);
        await using (var database = fixture.CreateContext())
        {
            await database.StockItems.Where(item => item.ProductId == id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Reserved, 3));
        }

        using var rejected = await fixture.PutAsync(id, 2);
        await AssertProblem(rejected, 409, "STOCK_BELOW_RESERVED");
        Assert.Equal(new InventoryResponse(id, 10, 3, 7),
            await fixture.Client.GetFromJsonAsync<InventoryResponse>($"/api/inventory/{id}"));
        Assert.Single(await fixture.GetMovementsAsync(id));
        Assert.Equal(new InventoryResponse(id, 8, 3, 5), await fixture.SetStockAsync(id, 8));
        var movement = (await fixture.GetMovementsAsync(id))[^1];
        Assert.Equal(3, movement.ReservedAfter);
        Assert.Equal(0, movement.ReservedDelta);
    }

    [Fact]
    public async Task ConcurrentFirstPutsCreateOneStockItemAndOneInitialMovement()
    {
        var id = fixture.AddProduct();
        fixture.Products.WaitForTwoLookups(id);
        var responses = await Task.WhenAll(fixture.PutAsync(id, 10), fixture.PutAsync(id, 10));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(2, fixture.Products.CallCount(id));
            await using var database = fixture.CreateContext();
            var stock = Assert.Single(await database.StockItems.Where(item => item.ProductId == id).ToListAsync());
            Assert.Equal(10, stock.OnHand);
            Assert.Equal(0, stock.Reserved);
            Assert.Single(await fixture.GetMovementsAsync(id));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LedgerFailureRollsBackTheWholeStockTransaction(bool existing)
    {
        var id = fixture.AddProduct();
        if (existing)
        {
            await fixture.SetStockAsync(id, 10);
        }

        await using var database = fixture.CreateContext();
        await database.Database.ExecuteSqlRawAsync("""
            ALTER TABLE dbo.StockMovements ADD CONSTRAINT CK_Test_LedgerFailure
            CHECK ([Reason] <> N'Fail ledger write');
            """);
        try
        {
            using var response = await fixture.PutAsync(id, 20, "Fail ledger write");
            await AssertProblem(response, 500, "INTERNAL_ERROR");
            var stock = await database.StockItems.AsNoTracking().SingleOrDefaultAsync(item => item.ProductId == id);
            if (existing)
            {
                Assert.NotNull(stock);
                Assert.Equal(10, stock.OnHand);
                Assert.Single(await fixture.GetMovementsAsync(id));
            }
            else
            {
                Assert.Null(stock);
                Assert.False(await database.StockMovements.AnyAsync(item => item.ProductId == id));
            }
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.StockMovements DROP CONSTRAINT CK_Test_LedgerFailure;");
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    public async Task SqlRejectsInvalidBalancesWithoutApiValidation(int onHand, int reserved)
    {
        await using var database = fixture.CreateContext();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var exception = await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO StockItems (ProductId, OnHand, Reserved, UpdatedAtUtc) VALUES ({id}, {onHand}, {reserved}, {now})"));
        Assert.Equal(547, exception.Number);
    }

    [Fact]
    public async Task LedgerCompositeIndexAllowsOneOperationAcrossProductsButRejectsDuplicateLines()
    {
        await using var database = fixture.CreateContext();
        var operationId = Guid.NewGuid();
        var firstProduct = Guid.NewGuid();
        var secondProduct = Guid.NewGuid();
        database.StockMovements.AddRange(Movement(firstProduct), Movement(secondProduct));
        await database.SaveChangesAsync();
        database.StockMovements.Add(Movement(firstProduct));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        Assert.IsType<SqlException>(exception.InnerException);
        Assert.Contains("IX_StockMovements_OperationId_ProductId_MovementType", exception.InnerException!.Message);

        StockMovement Movement(Guid productId)
        {
            return new StockMovement
            {
                OperationId = operationId,
                ProductId = productId,
                MovementType = "InitialStock",
                OnHandDelta = 1,
                OnHandAfter = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    [Fact]
    public async Task StockSetSupportsIntegerBoundaryWithoutDeltaOverflow()
    {
        var id = fixture.AddProduct();
        await fixture.SetStockAsync(id, int.MaxValue);
        await fixture.SetStockAsync(id, 0);
        var movements = await fixture.GetMovementsAsync(id);
        Assert.Equal(int.MaxValue, movements[0].OnHandDelta);
        Assert.Equal(-int.MaxValue, movements[1].OnHandDelta);
    }

    private static async Task AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
    }
}

public sealed class InventoryApiFixture : IAsyncLifetime
{
    private readonly MsSqlContainer sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private InventoryApiFactory? factory;
    public ProductStub Products { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public InventoryServiceContext CreateContext()
    {
        var connection = new SqlConnectionStringBuilder(sql.GetConnectionString()) { InitialCatalog = "InventoryDb" };
        var options = new DbContextOptionsBuilder<InventoryServiceContext>().UseSqlServer(connection.ConnectionString).Options;
        return new InventoryServiceContext(options);
    }

    public Guid AddProduct()
    {
        var id = Guid.NewGuid();
        Products.AddProduct(id);
        return id;
    }

    public Task<HttpResponseMessage> PutAsync(Guid id, int quantity, string reason = "Stocktake")
    {
        return Client.PutAsJsonAsync($"/api/inventory/{id}", new SetStockRequest { OnHand = quantity, Reason = reason });
    }

    public async Task<InventoryResponse> SetStockAsync(Guid id, int quantity, string reason = "Stocktake")
    {
        using var response = await PutAsync(id, quantity, reason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InventoryResponse>())!;
    }

    public async Task<StockMovementResponse[]> GetMovementsAsync(Guid id)
    {
        return (await Client.GetFromJsonAsync<StockMovementResponse[]>($"/api/inventory/{id}/movements"))!;
    }

    public async Task InitializeAsync()
    {
        await sql.StartAsync();
        await using var database = CreateContext();
        await database.Database.MigrateAsync();
        factory = new InventoryApiFactory(database.Database.GetConnectionString()!, Products);
        Client = factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        Products.Dispose();
        await sql.DisposeAsync();
    }
}
