using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Inventory;

public sealed class CreateReservationRequest : IValidatableObject
{
    [Required]
    public Guid? ReservationId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(50)]
    public ReservationItemRequest[]? Items { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ReservationId == Guid.Empty)
        {
            yield return new ValidationResult("Reservation ID must not be empty.", [nameof(ReservationId)]);
        }

        if (Items is null)
        {
            yield break;
        }

        if (Items.Any(item => item is null))
        {
            yield return new ValidationResult("Reservation items must not be null.", [nameof(Items)]);
            yield break;
        }

        if (Items.Select(item => item.ProductId).Distinct().Count() != Items.Length)
        {
            yield return new ValidationResult("Product IDs must be distinct.", [nameof(Items)]);
        }
    }
}

public sealed class ReservationItemRequest : IValidatableObject
{
    public Guid ProductId { get; init; }

    [Range(1, 10000)]
    public int Quantity { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ProductId == Guid.Empty)
        {
            yield return new ValidationResult("Product ID must not be empty.", [nameof(ProductId)]);
        }
    }
}
