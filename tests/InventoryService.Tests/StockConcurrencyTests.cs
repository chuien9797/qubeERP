using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shared.Contracts.Inventory;
using InventoryService.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryService.Tests;

[Trait("Category", "Integration")]
[Collection("Inventory SQL")]
public sealed class StockConcurrencyTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    [Fact]
    public async Task MigrationAssignsVersionsWithoutChangingExistingStockOrHistory()
    {
        await using var source = fixture.CreateContext();
        var connection = new SqlConnectionStringBuilder(source.Database.GetConnectionString())
            { InitialCatalog = "VersionUpgrade_" + Guid.NewGuid().ToString("N") };
        var options = new DbContextOptionsBuilder<InventoryServiceContext>().UseSqlServer(connection.ConnectionString).Options;
        await using var database = new InventoryServiceContext(options);
        try
        {
            var migrations = database.Database.GetMigrations().ToArray();
            await database.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            var product = Guid.NewGuid();
            var operation = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO dbo.StockItems (ProductId,OnHand,Reserved,UpdatedAtUtc) VALUES ({product},100,0,{now})");
            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO dbo.StockMovements (OperationId,ProductId,MovementType,OnHandDelta,ReservedDelta,OnHandAfter,ReservedAfter,Reason,CreatedAtUtc) VALUES ({operation},{product},N'InitialStock',100,0,100,0,N'Opening count',{now})");
            await database.Database.MigrateAsync();
            var stock = await database.StockItems.SingleAsync();
            Assert.Equal(100, stock.OnHand);
            Assert.Equal(0, stock.Reserved);
            Assert.Equal(now, stock.UpdatedAtUtc);
            Assert.Equal(8, stock.Version.Length);
            Assert.Equal(operation, (await database.StockMovements.SingleAsync()).OperationId);
            Assert.False(database.Database.HasPendingModelChanges());
        }
        finally { await database.Database.EnsureDeletedAsync(); }
    }

    [Fact]
    public async Task StaleCountCannotOverwriteReceiptAndFreshCountCanBeApplied()
    {
        var id = fixture.AddProduct();
        var original = await fixture.SetStockAsync(id, 100);
        using var receipt = await fixture.Client.PostAsJsonAsync($"/api/inventory/{id}/receipts",
            new ReceiveStockRequest { OperationId = Guid.NewGuid(), Quantity = 20, Reason = "Delivery" });
        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        await AssertConflict(await Put(id, 95, original.Version));
        var refreshed = await Balance(id);
        Assert.Equal(120, refreshed.OnHand);
        Assert.NotEqual(original.Version, refreshed.Version);
        Assert.Equal(2, (await fixture.GetMovementsAsync(id)).Length);
        using var applied = await Put(id, 95, refreshed.Version);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(95, (await Balance(id)).OnHand);
        Assert.Equal(-25, (await fixture.GetMovementsAsync(id))[^1].OnHandDelta);
    }

    [Fact]
    public async Task ExistingCountRequiresVersionEvenWhenQuantityIsUnchanged()
    {
        var id = fixture.AddProduct();
        var before = await fixture.SetStockAsync(id, 10);
        await AssertConflict(await Put(id, 10, null));
        Assert.Equal(before, await Balance(id));
        Assert.Single(await fixture.GetMovementsAsync(id));
    }

    [Fact]
    public async Task OnlyOneCountWithTheSameVersionCommits()
    {
        var id = fixture.AddProduct();
        var before = await fixture.SetStockAsync(id, 100);
        var responses = await Task.WhenAll(Put(id, 95, before.Version), Put(id, 90, before.Version));
        try
        {
            var success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await AssertConflict(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict), dispose: false);
            var winningBalance = (await success.Content.ReadFromJsonAsync<InventoryResponse>())!;
            Assert.Equal(winningBalance, await Balance(id));
            Assert.Equal(2, (await fixture.GetMovementsAsync(id)).Length);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ReservationAndReleaseBothInvalidateAViewedCount()
    {
        var id = fixture.AddProduct();
        var original = await fixture.SetStockAsync(id, 100);
        var reservationId = Guid.NewGuid();
        using var reserved = await fixture.Client.PostAsJsonAsync("/api/inventory/reservations",
            new CreateReservationRequest { ReservationId = reservationId,
                Items = [new ReservationItemRequest { ProductId = id, Quantity = 3 }] });
        Assert.Equal(HttpStatusCode.Created, reserved.StatusCode);
        var held = await Balance(id);
        Assert.NotEqual(original.Version, held.Version);
        await AssertConflict(await Put(id, 95, original.Version));
        using var release = await fixture.Client.PostAsJsonAsync($"/api/inventory/reservations/{reservationId}/release",
            new { reason = "Release hold" });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        var released = await Balance(id);
        Assert.NotEqual(held.Version, released.Version);
        Assert.Equal(100, released.OnHand);
        Assert.Equal(0, released.Reserved);
        await AssertConflict(await Put(id, 95, held.Version));
        Assert.Equal(3, (await fixture.GetMovementsAsync(id)).Length);
    }

    [Fact]
    public async Task SameCountWithCurrentVersionDoesNotRotateVersionOrAddMovement()
    {
        var id = fixture.AddProduct();
        var original = await fixture.SetStockAsync(id, 100);
        using var response = await Put(id, 100, original.Version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original, await response.Content.ReadFromJsonAsync<InventoryResponse>());
        Assert.Equal(original, await Balance(id));
        Assert.Single(await fixture.GetMovementsAsync(id));
    }

    [Fact]
    public async Task VersionForMissingStockDoesNotInitializeIt()
    {
        var id = fixture.AddProduct();
        using var response = await Put(id, 10, "AAAAAAAAAAE=");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, fixture.Products.CallCount(id));
        using var missing = await fixture.Client.GetAsync($"/api/inventory/{id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private Task<HttpResponseMessage> Put(Guid id, int quantity, string? version) =>
        fixture.Client.PutAsJsonAsync($"/api/inventory/{id}",
            new SetStockRequest { OnHand = quantity, Reason = "Physical count", ExpectedVersion = version });

    private async Task<InventoryResponse> Balance(Guid id) =>
        (await fixture.Client.GetFromJsonAsync<InventoryResponse>($"/api/inventory/{id}"))!;

    private static async Task AssertConflict(HttpResponseMessage response, bool dispose = true)
    {
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("STOCK_VERSION_CONFLICT", body.RootElement.GetProperty("errorCode").GetString());
        }
        finally { if (dispose) response.Dispose(); }
    }
}
