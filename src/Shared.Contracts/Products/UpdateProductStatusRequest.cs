using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Products;

public sealed class UpdateProductStatusRequest
{
    // Distinguishes missing/null input from the explicit boolean false.
    [Required]
    public bool? IsActive { get; init; }
}
