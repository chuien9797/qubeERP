using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ProductService.Data;
using ProductService.Domain;
using Shared.Contracts.Products;

namespace ProductService.Application;

public sealed class CatalogueService(ProductServiceContext database)
{
    public async Task<ProductResponse?> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var product = new Product
        {
            Sku = request.Sku!.Trim().ToUpperInvariant(),
            Name = request.Name!.Trim(),
            Description = request.Description,
            UnitPrice = request.UnitPrice!.Value
        };
        database.Products.Add(product);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException sql
            && sql.Number is 2601 or 2627 && sql.Message.Contains("IX_Products_Sku", StringComparison.Ordinal))
        {
            return null;
        }
        return ToResponse(product);
    }

    public async Task<ProductResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await database.Products.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        return product is null ? null : ToResponse(product);
    }

    public async Task<IReadOnlyList<ProductResponse>> ListAsync(int offset, int limit, CancellationToken cancellationToken,
        string? search = null, bool? isActive = null)
    {
        var query = database.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(value => value.Sku.Contains(term) || value.Name.Contains(term));
        }
        if (isActive.HasValue) query = query.Where(value => value.IsActive == isActive.Value);
        var products = await query.OrderBy(value => value.CreatedAtUtc)
            .ThenBy(value => value.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        return products.Select(ToResponse).ToArray();
    }

    public async Task<bool> SetStatusAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        // Atomic partial write; same-value requests do not change UpdatedAtUtc.
        var changed = await database.Products.Where(product => product.Id == id && product.IsActive != isActive)
            .ExecuteUpdateAsync(update => update.SetProperty(product => product.IsActive, isActive)
                .SetProperty(product => product.UpdatedAtUtc, _ => DateTimeOffset.UtcNow), cancellationToken);
        return changed > 0 || await database.Products.AnyAsync(product => product.Id == id, cancellationToken);
    }

    public async Task<ProductLookupResponse> LookupAsync(Guid[] ids, CancellationToken cancellationToken)
    {
        var products = await database.Products.AsNoTracking().Where(value => ids.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        return new ProductLookupResponse(
            ids.Where(products.ContainsKey).Select(id => ToResponse(products[id])).ToArray(),
            ids.Where(id => !products.ContainsKey(id)).ToArray());
    }

    private static ProductResponse ToResponse(Product product) => new(product.Id, product.Sku, product.Name,
        product.Description, product.UnitPrice, product.IsActive, product.CreatedAtUtc, product.UpdatedAtUtc);
}
