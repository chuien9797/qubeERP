using Shared.Contracts.Inventory;

namespace OrderService.Clients;

public interface IInventoryClient
{
    Task<InventoryResult> ReserveAsync(CreateReservationRequest request, CancellationToken cancellationToken);
}

public enum InventoryOutcome
{
    Reserved,
    Rejected,
    InvalidRequest,
    PayloadConflict,
    UnexpectedResponse,
    Uncertain
}

public sealed record InventoryResult(InventoryOutcome Outcome, IReadOnlyList<StockShortfall>? Shortfalls = null);
