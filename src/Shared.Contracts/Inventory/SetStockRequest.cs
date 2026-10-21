using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Inventory;

public sealed class SetStockRequest : IValidatableObject
{
    [Required]
    [Range(0, int.MaxValue)]
    public int? OnHand { get; init; }

    [Required]
    public string? Reason { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Reason?.Trim().Length > 300)
        {
            yield return new ValidationResult("Reason must contain at most 300 characters.", [nameof(Reason)]);
        }
    }
}
