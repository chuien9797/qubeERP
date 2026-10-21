using System.Data;
using InventoryService.Data;
using InventoryService.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public enum StockReceiptStatus { Created, Replayed, Conflict, StockNotFound, CapacityExceeded }

public sealed record StockReceiptResult(StockReceiptStatus Status, StockReceiptResponse? Receipt = null);

public sealed class StockReceiptService(IDbContextFactory<InventoryServiceContext> contexts)
{
    public async Task<StockReceiptResult> ReceiveAsync(Guid productId, ReceiveStockRequest request,
        CancellationToken cancellationToken)
    {
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            try
            {
                return await ReceiveInTransactionAsync(productId, request, cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqlException sql
                && sql.Number is 2601 or 2627
                && (sql.Message.Contains("UX_StockMovements_ReceiptOperation", StringComparison.Ordinal)
                    || sql.Message.Contains("IX_StockMovements_OperationId_ProductId_MovementType", StringComparison.Ordinal)))
            {
                await using var database = await contexts.CreateDbContextAsync(cancellationToken);
                var winner = await FindAsync(database, request.OperationId!.Value, cancellationToken)
                    ?? throw new InvalidOperationException("Committed stock receipt was not found.");
                return Replay(winner, productId, request);
            }
        });
    }

    private async Task<StockReceiptResult> ReceiveInTransactionAsync(Guid productId, ReceiveStockRequest request,
        CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var existing = await FindAsync(database, request.OperationId!.Value, cancellationToken);
        if (existing is not null)
            return Replay(existing, productId, request);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var quantity = request.Quantity!.Value;
        int onHand;
        int reserved;
        await using (var command = database.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandTimeout = 30;
            command.CommandText = """
                UPDATE dbo.StockItems
                SET OnHand = OnHand + @Quantity, UpdatedAtUtc = @Now
                OUTPUT inserted.OnHand, inserted.Reserved
                WHERE ProductId = @ProductId AND OnHand <= @MaximumBeforeReceipt;
                """;
            command.Parameters.Add(new SqlParameter("@ProductId", SqlDbType.UniqueIdentifier) { Value = productId });
            command.Parameters.Add(new SqlParameter("@Quantity", SqlDbType.Int) { Value = quantity });
            command.Parameters.Add(new SqlParameter("@MaximumBeforeReceipt", SqlDbType.Int) { Value = int.MaxValue - quantity });
            command.Parameters.Add(new SqlParameter("@Now", SqlDbType.DateTimeOffset) { Value = now });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                var committed = await FindAsync(database, request.OperationId.Value, cancellationToken);
                if (committed is not null)
                    return Replay(committed, productId, request);
                var exists = await database.StockItems.AnyAsync(item => item.ProductId == productId, cancellationToken);
                return new StockReceiptResult(exists ? StockReceiptStatus.CapacityExceeded : StockReceiptStatus.StockNotFound);
            }
            onHand = reader.GetInt32(0);
            reserved = reader.GetInt32(1);
        }

        var movement = new StockMovement
        {
            OperationId = request.OperationId.Value,
            ProductId = productId,
            MovementType = "StockReceipt",
            OnHandDelta = quantity,
            ReservedDelta = 0,
            OnHandAfter = onHand,
            ReservedAfter = reserved,
            Reason = request.Reason!.Trim(),
            CreatedAtUtc = now
        };
        database.StockMovements.Add(movement);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StockReceiptResult(StockReceiptStatus.Created, ToResponse(movement));
    }

    private static Task<StockMovement?> FindAsync(InventoryServiceContext database, Guid operationId,
        CancellationToken cancellationToken) => database.StockMovements.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == operationId && item.MovementType == "StockReceipt", cancellationToken);

    private static StockReceiptResult Replay(StockMovement movement, Guid productId, ReceiveStockRequest request)
    {
        if (movement.ProductId != productId || movement.OnHandDelta != request.Quantity
            || !string.Equals(movement.Reason, request.Reason!.Trim(), StringComparison.Ordinal))
            return new StockReceiptResult(StockReceiptStatus.Conflict);
        return new StockReceiptResult(StockReceiptStatus.Replayed, ToResponse(movement));
    }

    private static StockReceiptResponse ToResponse(StockMovement movement) => new(movement.OperationId,
        movement.ProductId, movement.OnHandDelta, movement.Reason!, movement.OnHandAfter, movement.ReservedAfter,
        movement.OnHandAfter - movement.ReservedAfter, movement.CreatedAtUtc);
}
