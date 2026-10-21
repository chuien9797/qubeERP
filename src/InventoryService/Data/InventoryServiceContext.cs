using Microsoft.EntityFrameworkCore;
using InventoryService.Domain;

namespace InventoryService.Data;

public sealed class InventoryServiceContext(DbContextOptions<InventoryServiceContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationLine> ReservationLines => Set<ReservationLine>();

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

        var reservation = modelBuilder.Entity<Reservation>();
        reservation.ToTable("Reservations", table =>
        {
            table.HasCheckConstraint("CK_Reservations_Status", "[Status] IN ('Processing', 'Reserved', 'Rejected')");
            table.HasCheckConstraint("CK_Reservations_Outcome", """
                ([Status] = 'Rejected' AND [FailureCode] IS NOT NULL
                    AND [FailureCode] = 'INSUFFICIENT_STOCK' AND [FailureDetailsJson] IS NOT NULL)
                OR ([Status] IN ('Processing', 'Reserved') AND [FailureCode] IS NULL AND [FailureDetailsJson] IS NULL)
                """);
            table.HasCheckConstraint("CK_Reservations_FailureJson", "[FailureDetailsJson] IS NULL OR ISJSON([FailureDetailsJson]) = 1");
        });
        reservation.HasKey(item => item.Id);
        reservation.Property(item => item.Id).ValueGeneratedNever();
        reservation.Property(item => item.RequestFingerprint).HasColumnType("char(64)").IsRequired();
        reservation.Property(item => item.Status).HasMaxLength(24).IsRequired();
        reservation.Property(item => item.FailureCode).HasMaxLength(80);

        var line = modelBuilder.Entity<ReservationLine>();
        line.ToTable("ReservationLines", table => table.HasCheckConstraint(
            "CK_ReservationLines_Quantity", "[Quantity] >= 1 AND [Quantity] <= 10000"));
        line.HasKey(item => new { item.ReservationId, item.ProductId });
        line.HasOne<Reservation>().WithMany().HasForeignKey(item => item.ReservationId).OnDelete(DeleteBehavior.Restrict);
    }
}
