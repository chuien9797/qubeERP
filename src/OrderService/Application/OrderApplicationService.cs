using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderService.Clients;
using OrderService.Data;
using OrderService.Domain;
using Shared.Contracts.Inventory;
using Shared.Contracts.Orders;
using Shared.Contracts.Events;

namespace OrderService.Application;

public sealed class OrderApplicationService(
    IDbContextFactory<OrderServiceContext> contexts, IProductClient products, IInventoryClient inventory,
    ILogger<OrderApplicationService> logger)
{
    private const decimal MaximumAmount = 9999999999999999.99m;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OrderResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var order = await database.Orders.AsNoTracking().Include(value => value.Lines)
            .SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        return order is null ? null : ToResponse(order);
    }

    public async Task<IReadOnlyList<OrderResponse>> ListAsync(string? status, int offset, int limit, CancellationToken cancellationToken,
        bool newestFirst = false)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var query = database.Orders.AsNoTracking();
        if (status is not null) query = query.Where(order => order.Status == status);
        var sorted = newestFirst ? query.OrderByDescending(order => order.CreatedAtUtc).ThenByDescending(order => order.Id)
            : query.OrderBy(order => order.CreatedAtUtc).ThenBy(order => order.Id);
        var orders = await sorted
            .Skip(offset).Take(limit).Include(order => order.Lines).ToArrayAsync(cancellationToken);
        return orders.Select(ToResponse).ToArray();
    }

    public async Task<OrderResult> CancelAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        await using (var database = await contexts.CreateDbContextAsync(cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            await database.Orders.Where(order => order.Id == id && order.Status == "Confirmed")
                .ExecuteUpdateAsync(update => update.SetProperty(order => order.Status, "CancellationPending")
                    .SetProperty(order => order.CancellationReason, reason)
                    .SetProperty(order => order.UpdatedAtUtc, now), cancellationToken);
        }
        var order = await GetAsync(id, cancellationToken);
        if (order is null) return new OrderResult(ErrorCode: "ORDER_NOT_FOUND");
        if (order.Status == "Cancelled") return new OrderResult(order);
        if (order.Status != "CancellationPending") return new OrderResult(order, ErrorCode: "ORDER_NOT_CANCELLABLE");
        // Always send the durably accepted reason, even when this caller lost the claim.
        var result = await inventory.ReleaseAsync(id, order.CancellationReason!, cancellationToken);
        await ApplyReleaseAsync(id, result, cancellationToken);
        return new OrderResult(await GetAsync(id, cancellationToken),
            ErrorCode: result.Outcome is InventoryOutcome.ReleaseConflict or InventoryOutcome.NotFound or InventoryOutcome.UnexpectedResponse
                ? "CANCELLATION_REQUIRES_REVIEW" : null);
    }

    public async Task<OrderResult> ReconcileCancellationAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await GetAsync(id, cancellationToken);
        if (order is null) return new OrderResult(ErrorCode: "ORDER_NOT_FOUND");
        if (order.Status == "Cancelled") return new OrderResult(order);
        if (order.Status != "CancellationPending") return new OrderResult(order, ErrorCode: "ORDER_NOT_CANCELLABLE");
        var result = await inventory.GetReleaseStatusAsync(id, cancellationToken);
        if (result.Outcome == InventoryOutcome.Reserved)
            result = await inventory.ReleaseAsync(id, order.CancellationReason!, cancellationToken);
        await ApplyReleaseAsync(id, result, cancellationToken);
        return new OrderResult(await GetAsync(id, cancellationToken));
    }

    private async Task ApplyReleaseAsync(Guid id, InventoryResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome == InventoryOutcome.Uncertain) return;
        var status = result.Outcome == InventoryOutcome.Released ? "Cancelled" : "RequiresReview";
        var code = status == "Cancelled" ? null : "CANCELLATION_INVENTORY_CONFLICT";
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var database = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var changed = await database.Orders.Where(order => order.Id == id && order.Status == "CancellationPending")
                .ExecuteUpdateAsync(update => update.SetProperty(order => order.Status, status)
                    .SetProperty(order => order.FailureCode, code).SetProperty(order => order.UpdatedAtUtc, now), cancellationToken);
            if (changed == 1 && status == "Cancelled")
            {
                var message = new OrderIntegrationEvent(Guid.NewGuid(), "OrderCancelled", 1, id, status, now);
                database.OutboxMessages.Add(new OutboxMessage
                {
                    Id = message.EventId, AggregateId = id, EventType = message.EventType, EventVersion = message.Version,
                    PayloadJson = JsonSerializer.Serialize(message, JsonOptions), CreatedAtUtc = now, NextAttemptAtUtc = now
                });
                await database.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task<OrderResult> CreateAsync(string key, CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var fingerprint = OrderFingerprint.Create(request.Items!);
        var existing = await FindByKeyAsync(key, cancellationToken);
        if (existing is not null)
        {
            return await ReplayAsync(existing, fingerprint, cancellationToken);
        }

        var lookup = await products.LookupAsync(request.Items!.Select(item => item.ProductId).ToArray(), cancellationToken);
        if (lookup is null)
        {
            return new OrderResult(ErrorCode: "PRODUCT_UNAVAILABLE");
        }

        var invalidIds = lookup.MissingProductIds.Concat(lookup.Products.Where(product => !product.IsActive)
            .Select(product => product.Id)).ToArray();
        if (invalidIds.Length > 0)
        {
            return new OrderResult(ErrorCode: "INVALID_PRODUCT", InvalidProductIds: invalidIds);
        }

        var id = Guid.NewGuid();
        var lines = new List<OrderLine>();
        var snapshots = lookup.Products.ToDictionary(product => product.Id);
        decimal total = 0;
        foreach (var item in request.Items!)
        {
            var product = snapshots[item.ProductId];
            var lineTotal = checked(product.UnitPrice * item.Quantity);
            total = checked(total + lineTotal);
            if (lineTotal > MaximumAmount || total > MaximumAmount)
            {
                return new OrderResult(ErrorCode: "ORDER_TOTAL_OUT_OF_RANGE");
            }

            lines.Add(new OrderLine
            {
                OrderId = id, ProductId = item.ProductId, Sku = product.Sku, Name = product.Name,
                UnitPrice = product.UnitPrice, Quantity = item.Quantity, LineTotal = lineTotal
            });
        }

        var order = new Order
        {
            Id = id, IdempotencyKey = key, RequestFingerprint = fingerprint,
            TotalAmount = total, Lines = lines
        };
        try
        {
            await SavePendingAsync(order, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException sql
            && sql.Number is 2601 or 2627 && sql.Message.Contains("IX_Orders_IdempotencyKey", StringComparison.Ordinal))
        {
            existing = await FindByKeyAsync(key, cancellationToken)
                ?? throw new InvalidOperationException("The winning order could not be loaded.", exception);
            return await ReplayAsync(existing, fingerprint, cancellationToken);
        }

        if (await RecordAttemptAsync(order.Id, cancellationToken))
        {
            var result = await inventory.ReserveAsync(ToReservationRequest(order), cancellationToken);
            await ApplyOutcomeAsync(order.Id, result, cancellationToken);
        }

        return new OrderResult(await GetAsync(order.Id, cancellationToken), IsNew: true);
    }

    private async Task SavePendingAsync(Order order, CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        database.Orders.Add(order);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<Order?> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        return await database.Orders.AsNoTracking().Include(value => value.Lines)
            .SingleOrDefaultAsync(value => value.IdempotencyKey == key, cancellationToken);
    }

    private async Task<bool> RecordAttemptAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return await database.Orders.Where(value => value.Id == id && value.Status == "Pending")
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.LastAttemptAtUtc, now), cancellationToken) > 0;
    }

    private async Task ApplyOutcomeAsync(Guid id, InventoryResult result, CancellationToken cancellationToken)
    {
        logger.LogInformation("Inventory returned {Outcome} for order {OrderId}", result.Outcome, id);
        if (result.Outcome is InventoryOutcome.InvalidRequest or InventoryOutcome.PayloadConflict or InventoryOutcome.UnexpectedResponse)
        {
            logger.LogError("Inventory contract failure {Outcome} for order {OrderId}", result.Outcome, id);
        }

        if (result.Outcome == InventoryOutcome.Uncertain)
        {
            return;
        }

        var status = result.Outcome switch
        {
            InventoryOutcome.Reserved => "Confirmed",
            InventoryOutcome.Rejected or InventoryOutcome.InvalidRequest => "Rejected",
            _ => "RequiresReview"
        };
        var code = result.Outcome switch
        {
            InventoryOutcome.Reserved => null,
            InventoryOutcome.Rejected => "INSUFFICIENT_STOCK",
            InventoryOutcome.InvalidRequest => "INVENTORY_INVALID_REQUEST",
            InventoryOutcome.PayloadConflict => "RESERVATION_PAYLOAD_CONFLICT",
            _ => "INVENTORY_UNEXPECTED_RESPONSE"
        };
        var details = result.Outcome == InventoryOutcome.Rejected ? JsonSerializer.Serialize(result.Shortfalls, JsonOptions) : null;
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var database = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var changed = await database.Orders.Where(value => value.Id == id && value.Status == "Pending")
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, status)
                    .SetProperty(value => value.FailureCode, code).SetProperty(value => value.FailureDetailsJson, details)
                    .SetProperty(value => value.UpdatedAtUtc, now), cancellationToken);
            if (changed == 1 && status is "Confirmed" or "Rejected")
            {
                var message = new OrderIntegrationEvent(Guid.NewGuid(), "Order" + status, 1, id, status, now);
                database.OutboxMessages.Add(new OutboxMessage
                {
                    Id = message.EventId, AggregateId = id, EventType = message.EventType, EventVersion = message.Version,
                    PayloadJson = JsonSerializer.Serialize(message, JsonOptions), CreatedAtUtc = now, NextAttemptAtUtc = now
                });
                await database.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task<OrderResult> ReplayAsync(Order order, string fingerprint, CancellationToken cancellationToken)
    {
        if (!string.Equals(order.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new OrderResult(ToResponse(order), ErrorCode: "IDEMPOTENCY_KEY_CONFLICT");
        }

        if (order.Status != "Pending")
        {
            return new OrderResult(ToResponse(order));
        }

        if (await RecordAttemptAsync(order.Id, cancellationToken))
        {
            var request = ToReservationRequest(order);
            var result = await inventory.GetAsync(order.Id, request.Items!, cancellationToken);
            if (result.Outcome == InventoryOutcome.NotFound)
            {
                result = await inventory.ReserveAsync(request, cancellationToken);
            }

            await ApplyOutcomeAsync(order.Id, result, cancellationToken);
        }

        return new OrderResult(await GetAsync(order.Id, cancellationToken));
    }

    private static CreateReservationRequest ToReservationRequest(Order order) => new()
    {
        ReservationId = order.Id,
        Items = order.Lines.Select(line => new ReservationItemRequest
        {
            ProductId = line.ProductId,
            Quantity = line.Quantity
        }).ToArray()
    };

    private static OrderResponse ToResponse(Order order) => new(order.Id, order.Status, order.TotalAmount,
        order.Lines.OrderBy(line => line.ProductId.ToString("D"), StringComparer.Ordinal).Select(line =>
            new OrderLineResponse(line.ProductId, line.Sku, line.Name, line.UnitPrice, line.Quantity, line.LineTotal)).ToArray(),
        order.FailureCode, order.FailureDetailsJson is null ? []
            : JsonSerializer.Deserialize<StockShortfall[]>(order.FailureDetailsJson, JsonOptions)!,
        order.CreatedAtUtc, order.UpdatedAtUtc, order.LastAttemptAtUtc, order.CancellationReason);
}
