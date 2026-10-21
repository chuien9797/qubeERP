using Microsoft.EntityFrameworkCore;
using InventoryService.Domain;

namespace InventoryService.Data;

public sealed class InventoryServiceContext(DbContextOptions<InventoryServiceContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var stock = modelBuilder.Entity<StockItem>();
        stock.ToTable("StockItems", table => table.HasCheckConstraint(
            "CK_StockItems_Balances", "[OnHand] >= 0 AND [Reserved] >= 0 AND [Reserved] <= [OnHand]"));
        stock.HasKey(item => item.ProductId);
        stock.Property(item => item.ProductId).ValueGeneratedNever();

        var movement = modelBuilder.Entity<StockMovement>();
        movement.ToTable("StockMovements", table =>
        {
            table.HasCheckConstraint("CK_StockMovements_Balances",
                "[OnHandAfter] >= 0 AND [ReservedAfter] >= 0 AND [ReservedAfter] <= [OnHandAfter]");
            table.HasCheckConstraint("CK_StockMovements_Type",
                "[MovementType] IN ('InitialStock', 'StockSet', 'Reserve')");
        });
        movement.HasKey(item => item.Id);
        movement.Property(item => item.MovementType).HasMaxLength(24).IsRequired();
        movement.Property(item => item.Reason).HasMaxLength(300);
        movement.HasIndex(item => new { item.OperationId, item.ProductId, item.MovementType }).IsUnique();
        movement.HasIndex(item => new { item.ProductId, item.Id });
    }
}
