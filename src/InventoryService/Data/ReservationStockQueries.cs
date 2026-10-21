using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InventoryService.Data;

internal static class ReservationStockQueries
{
    public static async Task<int> ReadAvailableAsync(
        InventoryServiceContext database, Guid productId, CancellationToken cancellationToken)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = 30;
        command.CommandText = """
            SELECT OnHand, Reserved
            FROM dbo.StockItems WITH (UPDLOCK, HOLDLOCK)
            WHERE ProductId = @ProductId;
            """;
        command.Parameters.Add(new SqlParameter("@ProductId", SqlDbType.UniqueIdentifier) { Value = productId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return 0;
        }

        return checked(reader.GetInt32(0) - reader.GetInt32(1));
    }

    public static async Task<(int OnHand, int Reserved)> ReserveAsync(
        InventoryServiceContext database, Guid productId, int quantity, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = 30;
        command.CommandText = """
            UPDATE dbo.StockItems
            SET Reserved = Reserved + @Quantity, UpdatedAtUtc = @Now
            OUTPUT inserted.OnHand, inserted.Reserved
            WHERE ProductId = @ProductId AND OnHand - Reserved >= @Quantity;
            """;
        command.Parameters.Add(new SqlParameter("@ProductId", SqlDbType.UniqueIdentifier) { Value = productId });
        command.Parameters.Add(new SqlParameter("@Quantity", SqlDbType.Int) { Value = quantity });
        command.Parameters.Add(new SqlParameter("@Now", SqlDbType.DateTimeOffset) { Value = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The guarded reservation update failed after the locked availability check.");
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
