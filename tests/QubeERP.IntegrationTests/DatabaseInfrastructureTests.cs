using InventoryService.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using ProductService.Data;
using System.Text.RegularExpressions;
using Testcontainers.MsSql;
using Xunit;

namespace QubeERP.IntegrationTests;

public sealed class DatabaseInfrastructureTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ServiceLoginsMigrateTheirOwnDatabasesAndCannotReadOtherServiceDatabases()
    {
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718")
            .Build();
        await sql.StartAsync();
        var credentials = new[]
        {
            (Database: "ProductDb", Login: "qube_product", Variable: "PRODUCT_DB_PASSWORD", Password: "Qube!" + Guid.NewGuid().ToString("N")),
            (Database: "InventoryDb", Login: "qube_inventory", Variable: "INVENTORY_DB_PASSWORD", Password: "Qube!" + Guid.NewGuid().ToString("N")),
            (Database: "OrderDb", Login: "qube_order", Variable: "ORDER_DB_PASSWORD", Password: "Qube!" + Guid.NewGuid().ToString("N"))
        };
        var setup = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Initialize-Databases.sql"));
        foreach (var credential in credentials)
        {
            setup = setup.Replace("$(" + credential.Variable + ")", credential.Password, StringComparison.Ordinal);
        }

        await using (var admin = new SqlConnection(sql.GetConnectionString()))
        {
            await admin.OpenAsync();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                foreach (var batch in Regex.Split("USE [master];\n" + setup, @"^GO\s*$", RegexOptions.Multiline))
                {
                    await using var command = new SqlCommand(batch, admin);
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        var connections = credentials.Select(credential => new SqlConnectionStringBuilder(sql.GetConnectionString())
        {
            InitialCatalog = credential.Database, UserID = credential.Login, Password = credential.Password
        }.ConnectionString).ToArray();
        await VerifyDatabase<ProductServiceContext>(connections[0], "ProductDb", 2, 1,
            options => new ProductServiceContext(options));
        await VerifyDatabase<InventoryServiceContext>(connections[1], "InventoryDb", 6, 4,
            options => new InventoryServiceContext(options));
        await VerifyDatabase<OrderServiceContext>(connections[2], "OrderDb", 4, 3,
            options => new OrderServiceContext(options));

        var tables = new[] { "ProductDb.dbo.Products", "InventoryDb.dbo.StockItems", "OrderDb.dbo.Orders" };
        for (var service = 0; service < connections.Length; service++)
        {
            await using var connection = new SqlConnection(connections[service]);
            await connection.OpenAsync();
            await using var role = new SqlCommand("SELECT IS_SRVROLEMEMBER('sysadmin');", connection);
            Assert.Equal(0, (int)(await role.ExecuteScalarAsync())!);
            for (var table = 0; table < tables.Length; table++)
            {
                await using var query = new SqlCommand("SELECT COUNT_BIG(*) FROM " + tables[table], connection);
                if (table == service)
                {
                    Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
                }
                else
                {
                    var error = await Assert.ThrowsAsync<SqlException>(() => query.ExecuteScalarAsync());
                    Assert.Equal(916, error.Number);
                }
            }
        }
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
