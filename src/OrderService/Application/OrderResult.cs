using Shared.Contracts.Orders;

namespace OrderService.Application;

public sealed record OrderResult(
    OrderResponse? Order = null, bool IsNew = false, string? ErrorCode = null,
    IReadOnlyList<Guid>? InvalidProductIds = null);
