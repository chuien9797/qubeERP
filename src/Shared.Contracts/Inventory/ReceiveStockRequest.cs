using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Inventory;

public sealed class ReceiveStockRequest : IValidatableObject
{
    [Required]
    public Guid? OperationId { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Quantity { get; init; }

    [Required]
    public string? Reason { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OperationId == Guid.Empty)
            yield return new ValidationResult("Operation ID must not be empty.", [nameof(OperationId)]);
        if (Reason?.Trim().Length > 300)
            yield return new ValidationResult("Reason must contain at most 300 characters.", [nameof(Reason)]);
    }
}
