using System.Data;
using InventoryService.Clients;
using InventoryService.Data;
using InventoryService.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public sealed class StockService(IDbContextFactory<InventoryServiceContext> contextFactory, IProductClient products)
{
    public async Task<InventoryResponse?> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await database.StockItems.AsNoTracking()
            .Where(item => item.ProductId == productId)
            .Select(item => new InventoryResponse(item.ProductId, item.OnHand, item.Reserved, item.OnHand - item.Reserved))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StockMovementResponse>> GetMovementsAsync(
        Guid productId, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await database.StockMovements.AsNoTracking()
            .Where(movement => movement.ProductId == productId)
            .OrderBy(movement => movement.Id)
            .Skip(offset)
            .Take(limit)
            .Select(movement => new StockMovementResponse(movement.Id, movement.OperationId, movement.ProductId,
                movement.MovementType, movement.OnHandDelta, movement.ReservedDelta, movement.OnHandAfter,
                movement.ReservedAfter, movement.Reason, movement.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<StockUpdateResult> SetAsync(
        Guid productId, int onHand, string reason, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(productId, cancellationToken);
        if (existing is not null)
        {
            return await UpdateAsync(productId, onHand, reason, cancellationToken);
        }

        var availability = await products.CheckExistsAsync(productId, cancellationToken);
        if (availability != ProductAvailability.Exists)
        {
            existing = await GetAsync(productId, cancellationToken);
            if (existing is not null)
            {
                return await UpdateAsync(productId, onHand, reason, cancellationToken);
            }

            var status = availability == ProductAvailability.Missing
                ? StockUpdateStatus.ProductNotFound
                : StockUpdateStatus.ProductUnavailable;
            return new StockUpdateResult(status);
        }

        if (await TryInitializeAsync(productId, onHand, reason, cancellationToken))
        {
            return new StockUpdateResult(StockUpdateStatus.Updated, new InventoryResponse(productId, onHand, 0, onHand));
        }

        return await UpdateAsync(productId, onHand, reason, cancellationToken);
    }

    private async Task<bool> TryInitializeAsync(
        Guid productId, int onHand, string reason, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                database.StockItems.Add(new StockItem
                {
                    ProductId = productId,
                    OnHand = onHand,
                    Reserved = 0,
                    UpdatedAtUtc = now
                });
                await database.SaveChangesAsync(cancellationToken);

                if (onHand > 0)
                {
                    database.StockMovements.Add(CreateMovement(productId, "InitialStock", onHand, onHand, 0, reason, now));
                    await database.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            });
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException sql
            && sql.Number is 2601 or 2627 && sql.Message.Contains("PK_StockItems", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private async Task<StockUpdateResult> UpdateAsync(
        Guid productId, int onHand, string reason, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var changed = await UpdateBalanceAsync(database, productId, onHand, now, cancellationToken);

            if (changed is null)
            {
                var current = await database.StockItems.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.ProductId == productId, cancellationToken);
                var status = current is null ? StockUpdateStatus.StockNotFound : StockUpdateStatus.BelowReserved;
                return new StockUpdateResult(status);
            }

            if (changed.Value.PreviousOnHand != onHand)
            {
                var delta = checked(onHand - changed.Value.PreviousOnHand);
                database.StockMovements.Add(CreateMovement(productId, "StockSet", delta, onHand,
                    changed.Value.Reserved, reason, now));
                await database.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var balance = new InventoryResponse(productId, onHand, changed.Value.Reserved,
                checked(onHand - changed.Value.Reserved));
            return new StockUpdateResult(StockUpdateStatus.Updated, balance);
        });
    }

    private static async Task<(int PreviousOnHand, int Reserved)?> UpdateBalanceAsync(
        InventoryServiceContext database, Guid productId, int onHand, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = 30;
        command.CommandText = """
            UPDATE dbo.StockItems
            SET OnHand = @OnHand,
                UpdatedAtUtc = CASE WHEN OnHand <> @OnHand THEN @Now ELSE UpdatedAtUtc END
            OUTPUT deleted.OnHand, inserted.OnHand, inserted.Reserved
            WHERE ProductId = @ProductId AND @OnHand >= Reserved;
            """;
        command.Parameters.Add(new SqlParameter("@ProductId", SqlDbType.UniqueIdentifier) { Value = productId });
        command.Parameters.Add(new SqlParameter("@OnHand", SqlDbType.Int) { Value = onHand });
        command.Parameters.Add(new SqlParameter("@Now", SqlDbType.DateTimeOffset) { Value = now });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt32(2));
    }

    private static StockMovement CreateMovement(Guid productId, string movementType, int delta,
        int onHand, int reserved, string reason, DateTimeOffset now)
    {
        return new StockMovement
        {
            OperationId = Guid.NewGuid(),
            ProductId = productId,
            MovementType = movementType,
            OnHandDelta = delta,
            ReservedDelta = 0,
            OnHandAfter = onHand,
            ReservedAfter = reserved,
            Reason = reason.Trim(),
            CreatedAtUtc = now
        };
    }
}
