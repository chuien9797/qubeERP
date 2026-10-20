using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Products;

public sealed class CreateProductRequest : IValidatableObject
{
    [Required]
    public string? Sku { get; init; }

    [Required]
    public string? Name { get; init; }

    public string? Description { get; init; }

    [Required]
    [Range(typeof(decimal), "0", "9999999999999999.99", ParseLimitsInInvariantCulture = true)]
    public decimal? UnitPrice { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Sku?.Trim().ToUpperInvariant().Length > 50)
            yield return new ValidationResult("SKU must contain at most 50 characters.", [nameof(Sku)]);
        if (Name?.Trim().Length > 200)
            yield return new ValidationResult("Name must contain at most 200 characters.", [nameof(Name)]);
        if (UnitPrice is decimal price && price != decimal.Round(price, 2))
            yield return new ValidationResult("Unit price must have at most two decimal places.", [nameof(UnitPrice)]);
    }
}
