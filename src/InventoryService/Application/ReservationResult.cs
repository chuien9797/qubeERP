using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public enum ReservationOutcome
{
    Reserved,
    Rejected,
    PayloadConflict,
    Unavailable
}

public sealed record ReservationResult(
    ReservationOutcome Outcome,
    ReservationResponse? Reservation = null,
    bool IsReplay = false);
