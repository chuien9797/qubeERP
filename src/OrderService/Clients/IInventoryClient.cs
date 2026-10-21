using Shared.Contracts.Inventory;

namespace OrderService.Clients;

public interface IInventoryClient
{
    Task<InventoryResult> ReserveAsync(CreateReservationRequest request, CancellationToken cancellationToken);
    Task<InventoryResult> GetAsync(Guid reservationId, IReadOnlyList<ReservationItemRequest> items, CancellationToken cancellationToken);
}

public enum InventoryOutcome
{
    Reserved,
    Rejected,
    InvalidRequest,
    PayloadConflict,
    UnexpectedResponse,
    Uncertain,
    NotFound
}

public sealed record InventoryResult(InventoryOutcome Outcome, IReadOnlyList<StockShortfall>? Shortfalls = null);
