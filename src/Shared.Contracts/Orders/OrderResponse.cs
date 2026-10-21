using Shared.Contracts.Inventory;

namespace Shared.Contracts.Orders;

public sealed record OrderLineResponse(Guid ProductId, string Sku, string Name, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record OrderResponse(
    Guid OrderId, string Status, decimal TotalAmount, IReadOnlyList<OrderLineResponse> Lines,
    string? FailureCode, IReadOnlyList<StockShortfall> Shortfalls,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? LastAttemptAtUtc);

public sealed record PendingOrderResponse(Guid OrderId, string Status, string StatusUrl);
