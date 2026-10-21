namespace Shared.Contracts.Inventory;

public sealed record StockReceiptResponse(Guid OperationId, Guid ProductId, int Quantity, string Reason,
    int OnHandAfter, int ReservedAfter, int AvailableQuantityAfter, DateTimeOffset CreatedAtUtc);
