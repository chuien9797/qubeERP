using System.Data;
using InventoryService.Data;
using InventoryService.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public sealed class ReservationService
{
    private readonly DbContextOptions<InventoryServiceContext> transactionOptions;
    private readonly ILogger<ReservationService> logger;

    public ReservationService(DbContextOptions<InventoryServiceContext> options, ILogger<ReservationService> logger)
    {
        transactionOptions = new DbContextOptionsBuilder<InventoryServiceContext>(options)
            .UseSqlServer(sql => sql.ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies)))
            .Options;
        this.logger = logger;
    }

    public async Task<ReservationResponse?> GetAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await FindAsync(reservationId, cancellationToken);
        return reservation is null ? null : ToResponse(reservation);
    }

    public async Task<ReservationResult> ReserveAsync(CreateReservationRequest request, CancellationToken cancellationToken)
    {
        var id = request.ReservationId!.Value;
        var items = request.Items!.OrderBy(item => item.ProductId.ToString("D"), StringComparer.Ordinal).ToArray();
        var fingerprint = ReservationFingerprint.Create(items);
        for (var attempt = 0; attempt <= 2; attempt++)
        {
            try
            {
                return await ReserveInTransactionAsync(id, items, fingerprint, cancellationToken);
            }
            catch (Exception exception) when (IsDeadlock(exception))
            {
                if (attempt == 2)
                {
                    logger.LogWarning(exception, "Reservation {ReservationId} exhausted its two deadlock retries", id);
                    return new ReservationResult(ReservationOutcome.Unavailable);
                }

                logger.LogWarning("Retrying reservation {ReservationId} after SQL deadlock, retry {Retry}", id, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException("Reservation retry loop ended without an outcome.");
    }

    private async Task<ReservationResult> ReserveInTransactionAsync(
        Guid id, ReservationItemRequest[] items, string fingerprint, CancellationToken cancellationToken)
    {
        await using var database = new InventoryServiceContext(transactionOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var reservation = new Reservation
        {
            Id = id,
            RequestFingerprint = fingerprint,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.Reservations.Add(reservation);
        await database.SaveChangesAsync(cancellationToken);

        var shortfalls = new List<StockShortfall>();
        foreach (var item in items)
        {
            var available = await ReservationStockQueries.ReadAvailableAsync(database, item.ProductId, cancellationToken);
            if (available < item.Quantity)
            {
                shortfalls.Add(new StockShortfall(item.ProductId, item.Quantity, available));
            }
        }

        foreach (var item in items)
        {
            database.ReservationLines.Add(new ReservationLine
            {
                ReservationId = id,
                ProductId = item.ProductId,
                Quantity = item.Quantity
            });
        }

        if (shortfalls.Count > 0)
        {
            return new ReservationResult(ReservationOutcome.Rejected,
                new ReservationResponse(id, "Rejected", "INSUFFICIENT_STOCK", shortfalls));
        }
        else
        {
            foreach (var item in items)
            {
                var balance = await ReservationStockQueries.ReserveAsync(database, item.ProductId, item.Quantity, now, cancellationToken);
                database.StockMovements.Add(new StockMovement
                {
                    OperationId = id,
                    ProductId = item.ProductId,
                    MovementType = "Reserve",
                    OnHandDelta = 0,
                    ReservedDelta = item.Quantity,
                    OnHandAfter = balance.OnHand,
                    ReservedAfter = balance.Reserved,
                    CreatedAtUtc = now
                });
            }

            reservation.Status = "Reserved";
        }

        reservation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Reservation {ReservationId} committed as {Status}", id, reservation.Status);
        var outcome = reservation.Status == "Reserved" ? ReservationOutcome.Reserved : ReservationOutcome.Rejected;
        return new ReservationResult(outcome, ToResponse(reservation));
    }

    private async Task<Reservation?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var database = new InventoryServiceContext(transactionOptions);
        return await database.Reservations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    private static ReservationResponse ToResponse(Reservation reservation)
    {
        if (reservation.Status is not ("Reserved" or "Rejected"))
        {
            throw new InvalidOperationException("A committed reservation must have a terminal outcome.");
        }

        return new ReservationResponse(reservation.Id, reservation.Status, null, []);
    }

    private static bool IsDeadlock(Exception exception)
    {
        return exception is SqlException { Number: 1205 }
            or DbUpdateException { InnerException: SqlException { Number: 1205 } };
    }
}
