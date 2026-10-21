using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderService.Clients;
using OrderService.Data;
using OrderService.Domain;
using Shared.Contracts.Inventory;
using Shared.Contracts.Orders;

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
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await database.Orders.Where(value => value.Id == id && value.Status == "Pending")
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, status)
                .SetProperty(value => value.FailureCode, code).SetProperty(value => value.FailureDetailsJson, details)
                .SetProperty(value => value.UpdatedAtUtc, now), cancellationToken);
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
        order.CreatedAtUtc, order.UpdatedAtUtc, order.LastAttemptAtUtc);
}
