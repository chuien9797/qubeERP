using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using InventoryService.Application;
using InventoryService.Clients;
using InventoryService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Shared.Contracts.Inventory;
using Xunit;

namespace InventoryService.Tests;

[Trait("Category", "Integration")]
[Collection("Inventory SQL")]
public sealed class StocktakeCommitUncertaintyTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommitAcknowledgementDoesNotReplayTheCountOverANewerReceipt(bool openingCount)
    {
        var product = fixture.AddProduct();
        if (!openingCount) await fixture.SetStockAsync(product, 10);
        await using var source = fixture.CreateContext();
        var interceptor = new LostCommitInterceptor(async () =>
        {
            using var receipt = await fixture.Client.PostAsJsonAsync($"/api/inventory/{product}/receipts",
                new ReceiveStockRequest { OperationId = Guid.NewGuid(), Quantity = 3, Reason = "Delivery after counted stock committed" });
            Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        });
        var options = new DbContextOptionsBuilder<InventoryServiceContext>()
            .UseSqlServer(source.Database.GetConnectionString(), sql => sql.EnableRetryOnFailure(2, TimeSpan.Zero, null))
            .AddInterceptors(interceptor).Options;
        var factory = new PooledDbContextFactory<InventoryServiceContext>(options);
        var service = new StockService(factory, new ExistingProduct(), options);
        await Assert.ThrowsAsync<TimeoutException>(() => service.SetAsync(product, 20, "Physical count", CancellationToken.None));
        Assert.Equal(1, interceptor.Commits);
        var balance = (await fixture.Client.GetFromJsonAsync<InventoryResponse>($"/api/inventory/{product}"))!;
        Assert.Equal(23, balance.OnHand);
        Assert.Equal(0, balance.Reserved);
        var movements = await fixture.GetMovementsAsync(product);
        Assert.Single(movements, movement => movement.MovementType == (openingCount ? "InitialStock" : "StockSet"));
        Assert.Single(movements, movement => movement.MovementType == "StockReceipt" && movement.OnHandDelta == 3);
    }

    private sealed class ExistingProduct : IProductClient
    {
        public Task<ProductAvailability> CheckExistsAsync(Guid productId, CancellationToken cancellationToken) => Task.FromResult(ProductAvailability.Exists);
    }

    private sealed class LostCommitInterceptor(Func<Task> afterCommit) : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            if (Commits != 1) return;
            await afterCommit();
            throw new TimeoutException("The database committed, but its acknowledgement was lost.");
        }
    }
}
