using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Inventory;

public sealed class ReleaseReservationRequest
{
    [Required, StringLength(500, MinimumLength = 1)]
    public string? Reason { get; init; }
}
