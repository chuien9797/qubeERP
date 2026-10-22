using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public enum ReleaseOutcome { Released, NotFound, Conflict, Unavailable }

public sealed record ReleaseResult(ReleaseOutcome Outcome, ReservationResponse? Reservation = null);
