using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Inventory;
using Xunit;

namespace InventoryService.Tests;

[Trait("Category", "Integration")]
[Collection("Inventory SQL")]
public sealed class ReleaseApiTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    private Task<HttpResponseMessage> Release(Guid id, string reason = "Customer cancelled") =>
        fixture.Client.PostAsJsonAsync($"/api/inventory/reservations/{id}/release", new ReleaseReservationRequest { Reason = reason });

    private async Task<(Guid Id, Guid[] Products)> ReserveAsync()
    {
        var products = new[] { fixture.AddProduct(), fixture.AddProduct() }
            .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        foreach (var product in products) await fixture.SetStockAsync(product, 10);
        var id = Guid.NewGuid();
        using var response = await fixture.Client.PostAsJsonAsync("/api/inventory/reservations", new CreateReservationRequest
        {
            ReservationId = id, Items = products.Select(product => new ReservationItemRequest { ProductId = product, Quantity = 3 }).ToArray()
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (id, products);
    }

    [Fact]
    public async Task ReleaseAllLinesAndConcurrentReplaysHaveOneEffectAndReconcileLedger()
    {
        var (id, products) = await ReserveAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 10).Select(async _ => { await start.Task; return await Release(id); }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(tasks);
        try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        using var replay = await Release(id, "Different reason");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var database = fixture.CreateContext();
        Assert.Equal("Released", (await database.Reservations.FindAsync(id))!.Status);
        var releases = await database.StockMovements.Where(row => row.OperationId == id && row.MovementType == "Release").ToArrayAsync();
        Assert.Equal(2, releases.Length);
        Assert.All(releases, row =>
        {
            Assert.Equal(0, row.OnHandDelta); Assert.Equal(-3, row.ReservedDelta);
            Assert.Equal(10, row.OnHandAfter); Assert.Equal(0, row.ReservedAfter);
            Assert.Equal("Customer cancelled", row.Reason);
        });
        foreach (var product in products)
        {
            var balance = await database.StockItems.SingleAsync(row => row.ProductId == product);
            Assert.Equal(10, balance.OnHand); Assert.Equal(0, balance.Reserved);
            var ledger = await database.StockMovements.Where(row => row.ProductId == product).ToArrayAsync();
            Assert.Equal(balance.OnHand, ledger.Sum(row => row.OnHandDelta));
            Assert.Equal(balance.Reserved, ledger.Sum(row => row.ReservedDelta));
        }
        // Original reserve replay must expose Released without creating a new hold or treating it as shortage.
        using var original = await fixture.Client.PostAsJsonAsync("/api/inventory/reservations", new CreateReservationRequest
        {
            ReservationId = id, Items = products.Select(product => new ReservationItemRequest { ProductId = product, Quantity = 3 }).ToArray()
        });
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal("Released", (await original.Content.ReadFromJsonAsync<ReservationResponse>())!.Status);
    }

    [Fact]
    public async Task FailureOnSecondLineRollsBackFirstLineStatusAndLedger()
    {
        var (id, products) = await ReserveAsync();
        await using var database = fixture.CreateContext();
        await database.StockItems.Where(row => row.ProductId == products[1]).ExecuteUpdateAsync(update => update.SetProperty(row => row.Reserved, 0));
        using var failed = await Release(id);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("Reserved", (await database.Reservations.AsNoTracking().SingleAsync(row => row.Id == id)).Status);
        Assert.Equal(3, (await database.StockItems.AsNoTracking().SingleAsync(row => row.ProductId == products[0])).Reserved);
        Assert.False(await database.StockMovements.AnyAsync(row => row.OperationId == id && row.MovementType == "Release"));
    }

    [Fact]
    public async Task ConsumedRejectedAndUnknownDoNotReleaseStock()
    {
        var (id, products) = await ReserveAsync();
        await using var database = fixture.CreateContext();
        await database.Reservations.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, "Consumed"));
        using var consumed = await Release(id);
        Assert.Equal(HttpStatusCode.Conflict, consumed.StatusCode);
        Assert.All(await database.StockItems.Where(row => products.Contains(row.ProductId)).ToArrayAsync(), row => Assert.Equal(3, row.Reserved));
        Assert.False(await database.StockMovements.AnyAsync(row => row.OperationId == id && row.MovementType == "Release"));
        var rejectedId = Guid.NewGuid();
        var request = new CreateReservationRequest { ReservationId = rejectedId,
            Items = [new ReservationItemRequest { ProductId = products[0], Quantity = 20 }] };
        using var rejected = await fixture.Client.PostAsJsonAsync("/api/inventory/reservations", request);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var before = await fixture.Client.GetStringAsync($"/api/inventory/reservations/{rejectedId}");
        using var rejectedRelease = await Release(rejectedId);
        Assert.Equal(HttpStatusCode.Conflict, rejectedRelease.StatusCode);
        Assert.Equal(before, await fixture.Client.GetStringAsync($"/api/inventory/reservations/{rejectedId}"));
        using var unknown = await Release(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReasonIsRequired(string? reason)
    {
        using var response = await fixture.Client.PostAsJsonAsync($"/api/inventory/reservations/{Guid.NewGuid()}/release", new { reason });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReasonOver500CharactersIsRejected()
    {
        using var response = await Release(Guid.NewGuid(), new string('x', 501));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
