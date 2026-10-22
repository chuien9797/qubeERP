namespace Shared.Contracts.Inventory;

public sealed record StockShortfall(Guid ProductId, int Requested, int Available);

public sealed record ReservationResponse(
    Guid ReservationId,
    string Status,
    string? FailureCode,
    IReadOnlyList<StockShortfall> Shortfalls);

public sealed record ReservationItemResponse(Guid ProductId, int Quantity);

public sealed record ReservationDetailsResponse(
    Guid ReservationId,
    string Status,
    string? FailureCode,
    IReadOnlyList<StockShortfall> Shortfalls,
    IReadOnlyList<ReservationItemResponse> Items);
