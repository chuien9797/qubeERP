using Microsoft.EntityFrameworkCore;
using ProductService.Domain;

namespace ProductService.Data;

public sealed class ProductServiceContext(DbContextOptions<ProductServiceContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var product = modelBuilder.Entity<Product>();
        product.ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_UnitPrice", "[UnitPrice] >= 0");
            table.HasCheckConstraint("CK_Products_Sku", "LEN(LTRIM(RTRIM([Sku]))) > 0");
            table.HasCheckConstraint("CK_Products_Name", "LEN(LTRIM(RTRIM([Name]))) > 0");
        });
        product.HasKey(value => value.Id);
        product.Property(value => value.Id).HasDefaultValueSql("NEWID()");
        product.Property(value => value.Sku).HasMaxLength(50).IsRequired();
        product.HasIndex(value => value.Sku).IsUnique();
        product.Property(value => value.Name).HasMaxLength(200).IsRequired();
        product.Property(value => value.UnitPrice).HasPrecision(18, 2);
        product.Property(value => value.IsActive).HasDefaultValue(true);
        product.Property(value => value.CreatedAtUtc).HasDefaultValueSql("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        product.Property(value => value.UpdatedAtUtc).HasDefaultValueSql("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
    }
}
