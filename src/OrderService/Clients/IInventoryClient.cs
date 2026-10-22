using Shared.Contracts.Inventory;

namespace OrderService.Clients;

public interface IInventoryClient
{
    Task<InventoryResult> ReserveAsync(CreateReservationRequest request, CancellationToken cancellationToken);
    Task<InventoryResult> GetAsync(Guid reservationId, IReadOnlyList<ReservationItemRequest> items, CancellationToken cancellationToken);
    Task<InventoryResult> ReleaseAsync(Guid reservationId, string reason, CancellationToken cancellationToken);
    Task<InventoryResult> GetReleaseStatusAsync(Guid reservationId, CancellationToken cancellationToken);
}

public enum InventoryOutcome
{
    Reserved,
    Rejected,
    InvalidRequest,
    PayloadConflict,
    UnexpectedResponse,
    Uncertain,
    NotFound,
    Released,
    ReleaseConflict
}

public sealed record InventoryResult(InventoryOutcome Outcome, IReadOnlyList<StockShortfall>? Shortfalls = null);
