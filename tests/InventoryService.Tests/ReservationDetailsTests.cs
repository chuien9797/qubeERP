using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Inventory;
using Xunit;

namespace InventoryService.Tests;

[Trait("Category", "Integration")]
[Collection("Inventory SQL")]
public sealed class ReservationDetailsTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Reserved")]
    [InlineData("Released")]
    [InlineData("Consumed")]
    [InlineData("Rejected")]
    public async Task RepeatedReadsReturnPersistedOriginalItemsAndNeverChangeInventory(string status)
    {
        var products = new[] { fixture.AddProduct(), fixture.AddProduct() };
        await fixture.SetStockAsync(products[0], 20);
        await fixture.SetStockAsync(products[1], status == "Rejected" ? 1 : 20);
        var id = Guid.NewGuid();
        using var created = await Reserve(id, products, 3, 7);
        Assert.Equal(status == "Rejected" ? HttpStatusCode.Conflict : HttpStatusCode.Created, created.StatusCode);

        await using var database = fixture.CreateContext();
        var saved = await database.Reservations.AsNoTracking().SingleAsync(row => row.Id == id);
        var originalShortfalls = saved.FailureDetailsJson is null ? []
            : JsonSerializer.Deserialize<StockShortfall[]>(saved.FailureDetailsJson, JsonOptions)!;
        if (status == "Released")
        {
            using var released = await fixture.Client.PostAsJsonAsync($"/api/inventory/reservations/{id}/release",
                new ReleaseReservationRequest { Reason = "Details query test release" });
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        }
        else if (status == "Consumed")
        {
            // Consumption is outside scope; simulate its persisted terminal state only.
            await database.Reservations.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, "Consumed"));
        }

        // Restocking and another hold must never change the historical items or rejection shortfalls.
        await fixture.SetStockAsync(products[0], 50);
        await fixture.SetStockAsync(products[1], 60);
        using var another = await Reserve(Guid.NewGuid(), products, 4, 2);
        Assert.Equal(HttpStatusCode.Created, another.StatusCode);
        var productCalls = products.Select(fixture.Products.CallCount).ToArray();
        foreach (var product in products) fixture.Products.Fail(product, timeout: false);

        var before = await Snapshot(id, products);
        string? firstBody = null;
        var expectedItems = new[] { new ReservationItemResponse(products[0], 3), new ReservationItemResponse(products[1], 7) }
            .OrderBy(item => item.ProductId.ToString("D"), StringComparer.Ordinal).ToArray();
        for (var read = 0; read < 5; read++)
        {
            using var response = await fixture.Client.GetAsync($"/api/inventory/reservations/{id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (firstBody is not null) Assert.Equal(firstBody, body);
            firstBody = body;
            var details = JsonSerializer.Deserialize<ReservationDetailsResponse>(body, JsonOptions)!;
            Assert.Equal(id, details.ReservationId);
            Assert.Equal(status, details.Status);
            Assert.Equal(expectedItems, details.Items);
            Assert.Equal(saved.FailureCode, details.FailureCode);
            Assert.Equal(originalShortfalls, details.Shortfalls);
            if (status == "Rejected") Assert.Equal(new StockShortfall(products[1], 7, 1), Assert.Single(details.Shortfalls));

            // Existing OrderService clients deserialize the unchanged outcome DTO and ignore new fields.
            var legacy = JsonSerializer.Deserialize<ReservationResponse>(body, JsonOptions)!;
            Assert.Equal(details.ReservationId, legacy.ReservationId);
            Assert.Equal(details.Status, legacy.Status);
            Assert.Equal(details.FailureCode, legacy.FailureCode);
            Assert.Equal(details.Shortfalls, legacy.Shortfalls);
        }
        Assert.Equal(before, await Snapshot(id, products));
        Assert.Equal(productCalls, products.Select(fixture.Products.CallCount));
    }

    private Task<HttpResponseMessage> Reserve(Guid id, Guid[] products, int first, int second) =>
        fixture.Client.PostAsJsonAsync("/api/inventory/reservations", new CreateReservationRequest
        {
            ReservationId = id, Items = [new ReservationItemRequest { ProductId = products[1], Quantity = second },
                new ReservationItemRequest { ProductId = products[0], Quantity = first }]
        });

    private async Task<string> Snapshot(Guid id, Guid[] products)
    {
        await using var database = fixture.CreateContext();
        var header = await database.Reservations.AsNoTracking().SingleAsync(row => row.Id == id);
        var lines = await database.ReservationLines.AsNoTracking().Where(row => row.ReservationId == id).OrderBy(row => row.ProductId).ToArrayAsync();
        var stock = await database.StockItems.AsNoTracking().Where(row => products.Contains(row.ProductId)).OrderBy(row => row.ProductId).ToArrayAsync();
        var ledger = await database.StockMovements.AsNoTracking().Where(row => products.Contains(row.ProductId)).OrderBy(row => row.Id).ToArrayAsync();
        return JsonSerializer.Serialize(new { header, lines, stock, ledger });
    }
}
