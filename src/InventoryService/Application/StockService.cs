using System.Data;
using InventoryService.Clients;
using InventoryService.Data;
using InventoryService.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public sealed class StockService(IDbContextFactory<InventoryServiceContext> contextFactory, IProductClient products,
    DbContextOptions<InventoryServiceContext> options)
{
    // Physical counts use a non-retrying write context.
    private readonly DbContextOptions<InventoryServiceContext> writeOptions = new DbContextOptionsBuilder<InventoryServiceContext>(options)
        .UseSqlServer(sql => sql.ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies))).Options;
    public async Task<InventoryResponse?> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await database.StockItems.AsNoTracking()
            .Where(item => item.ProductId == productId)
            .Select(item => new InventoryResponse(item.ProductId, item.OnHand, item.Reserved, item.OnHand - item.Reserved, Convert.ToBase64String(item.Version)))
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
        Guid productId, int onHand, string reason, string? expectedVersion, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(productId, cancellationToken);
        if (existing is not null)
            return await UpdateAsync(productId, onHand, reason, expectedVersion, cancellationToken);
        if (expectedVersion is not null) return new StockUpdateResult(StockUpdateStatus.StockNotFound);

        var availability = await products.CheckExistsAsync(productId, cancellationToken);
        if (availability != ProductAvailability.Exists)
        {
            existing = await GetAsync(productId, cancellationToken);
            if (existing is not null) return new StockUpdateResult(StockUpdateStatus.VersionConflict);
            return new StockUpdateResult(availability == ProductAvailability.Missing
                ? StockUpdateStatus.ProductNotFound : StockUpdateStatus.ProductUnavailable);
        }

        try
        {
            await using var database = new InventoryServiceContext(writeOptions);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var item = new StockItem { ProductId = productId, OnHand = onHand, UpdatedAtUtc = DateTimeOffset.UtcNow };
            database.StockItems.Add(item);
            await database.SaveChangesAsync(cancellationToken);
            if (onHand > 0)
            {
                database.StockMovements.Add(CreateMovement(productId, "InitialStock", onHand, onHand, 0, reason, item.UpdatedAtUtc));
                await database.SaveChangesAsync(cancellationToken);
            }
            var response = ToBalance(item);
            await transaction.CommitAsync(cancellationToken);
            return new StockUpdateResult(StockUpdateStatus.Updated, response);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException sql
            && sql.Number is 2601 or 2627 && sql.Message.Contains("PK_StockItems", StringComparison.Ordinal))
        {
            return new StockUpdateResult(StockUpdateStatus.VersionConflict);
        }
    }

    private async Task<StockUpdateResult> UpdateAsync(
        Guid productId, int onHand, string reason, string? expectedVersion, CancellationToken cancellationToken)
    {
        await using var database = new InventoryServiceContext(writeOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var current = await database.StockItems.FromSqlInterpolated(
            $"SELECT * FROM dbo.StockItems WITH (UPDLOCK, HOLDLOCK) WHERE ProductId = {productId}")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (current is null) return new StockUpdateResult(StockUpdateStatus.StockNotFound);
        if (expectedVersion is null || !current.Version.SequenceEqual(Convert.FromBase64String(expectedVersion)))
            return new StockUpdateResult(StockUpdateStatus.VersionConflict);
        if (onHand < current.Reserved) return new StockUpdateResult(StockUpdateStatus.BelowReserved);
        if (onHand == current.OnHand)
        {
            await transaction.CommitAsync(cancellationToken);
            return new StockUpdateResult(StockUpdateStatus.Updated, ToBalance(current));
        }

        var previous = current.OnHand;
        await using (var command = database.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandTimeout = 30;
            command.CommandText = """
                UPDATE dbo.StockItems
                SET OnHand = @OnHand, UpdatedAtUtc = @Now
                OUTPUT inserted.Version
                WHERE ProductId = @ProductId AND Version = @Version AND @OnHand >= Reserved;
                """;
            command.Parameters.Add(new SqlParameter("@ProductId", SqlDbType.UniqueIdentifier) { Value = productId });
            command.Parameters.Add(new SqlParameter("@OnHand", SqlDbType.Int) { Value = onHand });
            command.Parameters.Add(new SqlParameter("@Now", SqlDbType.DateTimeOffset) { Value = DateTimeOffset.UtcNow });
            command.Parameters.Add(new SqlParameter("@Version", SqlDbType.Timestamp, 8) { Value = current.Version });
            var version = await command.ExecuteScalarAsync(cancellationToken) as byte[];
            if (version is null) return new StockUpdateResult(StockUpdateStatus.VersionConflict);
            current.Version = version;
        }
        current.OnHand = onHand;
        database.StockMovements.Add(CreateMovement(productId, "StockSet", checked(onHand - previous),
            onHand, current.Reserved, reason, DateTimeOffset.UtcNow));
        await database.SaveChangesAsync(cancellationToken);
        var balance = ToBalance(current);
        await transaction.CommitAsync(cancellationToken);
        return new StockUpdateResult(StockUpdateStatus.Updated, balance);
    }

    private static InventoryResponse ToBalance(StockItem item) => new(item.ProductId, item.OnHand,
        item.Reserved, item.OnHand - item.Reserved, Convert.ToBase64String(item.Version));

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
