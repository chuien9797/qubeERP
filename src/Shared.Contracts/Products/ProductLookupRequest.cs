using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Products;

public sealed class ProductLookupRequest : IValidatableObject
{
    [Required]
    [MinLength(1)]
    [MaxLength(50)]
    public Guid[]? ProductIds { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ProductIds is null)
            yield break;
        if (ProductIds.Contains(Guid.Empty))
            yield return new ValidationResult("Product IDs must not be empty GUIDs.", [nameof(ProductIds)]);
        if (ProductIds.Distinct().Count() != ProductIds.Length)
            yield return new ValidationResult("Product IDs must be distinct.", [nameof(ProductIds)]);
    }
}
