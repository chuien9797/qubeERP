namespace Shared.Contracts.Products;

public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    decimal UnitPrice,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProductLookupResponse(
    IReadOnlyList<ProductResponse> Products,
    IReadOnlyList<Guid> MissingProductIds);
