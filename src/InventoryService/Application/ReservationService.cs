using System.Data;
using System.Text.Json;
using InventoryService.Data;
using InventoryService.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public sealed class ReservationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DbContextOptions<InventoryServiceContext> transactionOptions;
    private readonly ILogger<ReservationService> logger;

    public ReservationService(DbContextOptions<InventoryServiceContext> options, ILogger<ReservationService> logger)
    {
        transactionOptions = new DbContextOptionsBuilder<InventoryServiceContext>(options)
            .UseSqlServer(sql => sql.ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies)))
            .Options;
        this.logger = logger;
    }

    public async Task<ReservationDetailsResponse?> GetAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        await using var database = new InventoryServiceContext(transactionOptions);
        var reservation = await database.Reservations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == reservationId, cancellationToken);
        if (reservation is null) return null;

        var lines = await database.ReservationLines.AsNoTracking()
            .Where(line => line.ReservationId == reservationId)
            .Select(line => new ReservationItemResponse(line.ProductId, line.Quantity))
            .ToArrayAsync(cancellationToken);
        var outcome = ToResponse(reservation);
        return new ReservationDetailsResponse(outcome.ReservationId, outcome.Status, outcome.FailureCode, outcome.Shortfalls,
            lines.OrderBy(line => line.ProductId.ToString("D"), StringComparer.Ordinal).ToArray());
    }

    public async Task<ReservationResult> ReserveAsync(CreateReservationRequest request, CancellationToken cancellationToken)
    {
        var id = request.ReservationId!.Value;
        var items = request.Items!.OrderBy(item => item.ProductId.ToString("D"), StringComparer.Ordinal).ToArray();
        var fingerprint = ReservationFingerprint.Create(items);
        var existing = await FindAsync(id, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, fingerprint);
        }

        for (var attempt = 0; attempt <= 2; attempt++)
        {
            try
            {
                return await ReserveInTransactionAsync(id, items, fingerprint, cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqlException sql
                && sql.Number is 2601 or 2627 && sql.Message.Contains("PK_Reservations", StringComparison.Ordinal))
            {
                existing = await FindAsync(id, cancellationToken);
                if (existing is not null)
                {
                    return Replay(existing, fingerprint);
                }

                logger.LogError("Reservation {ReservationId} disappeared after its primary-key conflict", id);
                return new ReservationResult(ReservationOutcome.Unavailable);
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

    public async Task<ReleaseResult> ReleaseAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReleaseInTransactionAsync(id, reason, cancellationToken);
            }
            catch (Exception exception) when (IsDeadlock(exception))
            {
                if (attempt == 2)
                {
                    logger.LogWarning(exception, "Release {ReservationId} exhausted its two deadlock retries", id);
                    return new ReleaseResult(ReleaseOutcome.Unavailable);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private async Task<ReleaseResult> ReleaseInTransactionAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        await using var database = new InventoryServiceContext(transactionOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var changed = await database.Reservations.Where(value => value.Id == id && value.Status == "Reserved")
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, "Released")
                .SetProperty(value => value.UpdatedAtUtc, now), cancellationToken);
        if (changed == 0)
        {
            var existing = await database.Reservations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
            return existing is null ? new ReleaseResult(ReleaseOutcome.NotFound)
                : existing.Status == "Released" ? new ReleaseResult(ReleaseOutcome.Released, ToResponse(existing))
                : new ReleaseResult(ReleaseOutcome.Conflict);
        }

        var lines = (await database.ReservationLines.AsNoTracking().Where(value => value.ReservationId == id)
            .ToArrayAsync(cancellationToken)).OrderBy(value => value.ProductId.ToString("D"), StringComparer.Ordinal).ToArray();
        if (lines.Length == 0) throw new InvalidOperationException("A reserved reservation has no persisted lines.");
        foreach (var line in lines)
        {
            var balance = await ReservationStockQueries.ReleaseAsync(database, line.ProductId, line.Quantity, now, cancellationToken);
            database.StockMovements.Add(new StockMovement
            {
                OperationId = id, ProductId = line.ProductId, MovementType = "Release",
                OnHandDelta = 0, ReservedDelta = -line.Quantity,
                OnHandAfter = balance.OnHand, ReservedAfter = balance.Reserved,
                Reason = reason, CreatedAtUtc = now
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReleaseResult(ReleaseOutcome.Released, new ReservationResponse(id, "Released", null, []));
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
            reservation.Status = "Rejected";
            reservation.FailureCode = "INSUFFICIENT_STOCK";
            reservation.FailureDetailsJson = JsonSerializer.Serialize(shortfalls, JsonOptions);
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

    private static ReservationResult Replay(Reservation reservation, string fingerprint)
    {
        if (!string.Equals(reservation.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new ReservationResult(ReservationOutcome.PayloadConflict);
        }

        var response = ToResponse(reservation);
        // Terminal outcomes preserve the original reservation.
        var outcome = response.Status == "Rejected" ? ReservationOutcome.Rejected : ReservationOutcome.Reserved;
        return new ReservationResult(outcome, response, IsReplay: true);
    }

    private static ReservationResponse ToResponse(Reservation reservation)
    {
        if (reservation.Status is not ("Reserved" or "Rejected" or "Released" or "Consumed"))
        {
            throw new InvalidOperationException("A committed reservation must have a terminal outcome.");
        }

        var shortfalls = reservation.Status == "Rejected"
            ? JsonSerializer.Deserialize<StockShortfall[]>(reservation.FailureDetailsJson!, JsonOptions)
                ?? throw new InvalidOperationException("Rejected reservation details are missing.")
            : [];
        return new ReservationResponse(reservation.Id, reservation.Status, reservation.FailureCode, shortfalls);
    }

    private static bool IsDeadlock(Exception exception)
    {
        return exception is SqlException { Number: 1205 }
            or DbUpdateException { InnerException: SqlException { Number: 1205 } };
    }
}
