using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Orders;

public sealed class CancelOrderRequest
{
    [Required, StringLength(500, MinimumLength = 1)]
    public string? Reason { get; init; }
}
