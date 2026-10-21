namespace Shared.Contracts.Inventory;

public sealed record StockShortfall(Guid ProductId, int Requested, int Available);

public sealed record ReservationResponse(
    Guid ReservationId,
    string Status,
    string? FailureCode,
    IReadOnlyList<StockShortfall> Shortfalls);
