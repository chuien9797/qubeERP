namespace Shared.Contracts.Inventory;

public sealed record InventoryResponse(Guid ProductId, int OnHand, int Reserved, int AvailableQuantity);

public sealed record StockMovementResponse(
    long Id,
    Guid OperationId,
    Guid ProductId,
    string MovementType,
    int OnHandDelta,
    int ReservedDelta,
    int OnHandAfter,
    int ReservedAfter,
    string? Reason,
    DateTimeOffset CreatedAtUtc);
