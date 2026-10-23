using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Inventory;

public sealed class SetStockRequest : IValidatableObject
{
    [Required]
    [Range(0, int.MaxValue)]
    public int? OnHand { get; init; }

    [Required]
    public string? Reason { get; init; }

    public string? ExpectedVersion { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ExpectedVersion is not null && (ExpectedVersion.Length != 12
            || !Convert.TryFromBase64String(ExpectedVersion, new byte[8], out var length) || length != 8))
            yield return new ValidationResult("Expected version must be an 8-byte Base64 stock version.", [nameof(ExpectedVersion)]);
        if (Reason?.Trim().Length > 300)
        {
            yield return new ValidationResult("Reason must contain at most 300 characters.", [nameof(Reason)]);
        }
    }
}
