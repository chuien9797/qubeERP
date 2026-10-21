using InventoryService.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using ProductService.Data;
using Testcontainers.MsSql;
using Xunit;

namespace QubeERP.IntegrationTests;

public sealed class DatabaseInfrastructureTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task MigrationsCreateThreeIndependentDatabasesAndCanBeReapplied()
    {
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();
        await sql.StartAsync();
        await VerifyDatabase<ProductServiceContext>(sql.GetConnectionString(), "ProductDb", 2, 1,
            options => new ProductServiceContext(options));
        await VerifyDatabase<InventoryServiceContext>(sql.GetConnectionString(), "InventoryDb", 3, 4,
            options => new InventoryServiceContext(options));
        await VerifyDatabase<OrderServiceContext>(sql.GetConnectionString(), "OrderDb", 1, 0,
            options => new OrderServiceContext(options));
    }

    private static async Task VerifyDatabase<TContext>(string connectionString, string databaseName, int migrationCount, int entityCount,
        Func<DbContextOptions<TContext>, TContext> createContext) where TContext : DbContext
    {
        var connection = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = databaseName };
        var options = new DbContextOptionsBuilder<TContext>().UseSqlServer(connection.ConnectionString).Options;
        await using var context = createContext(options);
        Assert.False(context.Database.HasPendingModelChanges());
        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();
        Assert.True(await context.Database.CanConnectAsync());
        Assert.Equal(migrationCount, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(entityCount, context.Model.GetEntityTypes().Count());
    }
}
