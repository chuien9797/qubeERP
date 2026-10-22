using Microsoft.EntityFrameworkCore;
using OrderService.Domain;

namespace OrderService.Data;

public sealed class OrderServiceContext(DbContextOptions<OrderServiceContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<Order>();
        order.ToTable("Orders", table =>
        {
            table.HasCheckConstraint("CK_Orders_Status", "[Status] IN ('Pending', 'Confirmed', 'Rejected', 'RequiresReview', 'CancellationPending', 'Cancelled', 'Fulfilled')");
            table.HasCheckConstraint("CK_Orders_CancellationReason",
                "[Status] NOT IN ('CancellationPending', 'Cancelled') OR ([CancellationReason] IS NOT NULL AND LEN(LTRIM(RTRIM([CancellationReason]))) > 0)");
            table.HasCheckConstraint("CK_Orders_Total", "[TotalAmount] >= 0");
            table.HasCheckConstraint("CK_Orders_Outcome", """
                ([Status] IN ('Pending', 'Confirmed', 'CancellationPending', 'Cancelled', 'Fulfilled') AND [FailureCode] IS NULL AND [FailureDetailsJson] IS NULL)
                OR ([Status] = 'Rejected' AND [FailureCode] IS NOT NULL AND
                    (([FailureCode] = 'INSUFFICIENT_STOCK' AND [FailureDetailsJson] IS NOT NULL)
                    OR ([FailureCode] = 'INVENTORY_INVALID_REQUEST' AND [FailureDetailsJson] IS NULL)))
                OR ([Status] = 'RequiresReview' AND [FailureCode] IS NOT NULL)
                """);
            table.HasCheckConstraint("CK_Orders_FailureJson", "[FailureDetailsJson] IS NULL OR ISJSON([FailureDetailsJson]) = 1");
            table.HasCheckConstraint("CK_Orders_Key", "LEN([IdempotencyKey]) > 0");
        });
        order.HasKey(value => value.Id);
        order.Property(value => value.Id).ValueGeneratedNever();
        order.Property(value => value.IdempotencyKey).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        order.HasIndex(value => value.IdempotencyKey).IsUnique();
        order.Property(value => value.RequestFingerprint).HasColumnType("char(64)").IsRequired();
        order.Property(value => value.Status).HasMaxLength(24).IsRequired();
        order.Property(value => value.TotalAmount).HasPrecision(18, 2);
        order.Property(value => value.FailureCode).HasMaxLength(80);
        order.Property(value => value.CancellationReason).HasMaxLength(500);
        order.HasMany(value => value.Lines).WithOne().HasForeignKey(value => value.OrderId).OnDelete(DeleteBehavior.Restrict);

        var line = modelBuilder.Entity<OrderLine>();
        line.ToTable("OrderLines", table =>
        {
            table.HasCheckConstraint("CK_OrderLines_Quantity", "[Quantity] >= 1 AND [Quantity] <= 10000");
            table.HasCheckConstraint("CK_OrderLines_Amounts", "[UnitPrice] >= 0 AND [LineTotal] >= 0 AND [LineTotal] = [UnitPrice] * [Quantity]");
        });
        line.HasKey(value => new { value.OrderId, value.ProductId });
        line.Property(value => value.Sku).HasMaxLength(50).IsRequired();
        line.Property(value => value.Name).HasMaxLength(200).IsRequired();
        line.Property(value => value.UnitPrice).HasPrecision(18, 2);
        line.Property(value => value.LineTotal).HasPrecision(18, 2);

        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages", table =>
        {
            table.HasCheckConstraint("CK_OutboxMessages_Event", "[EventType] IN ('OrderConfirmed', 'OrderRejected', 'OrderCancelled') AND [EventVersion] = 1");
            table.HasCheckConstraint("CK_OutboxMessages_Payload", "ISJSON([PayloadJson]) = 1");
            table.HasCheckConstraint("CK_OutboxMessages_Attempts", "[AttemptCount] >= 0");
        });
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).ValueGeneratedNever();
        outbox.Property(value => value.EventType).HasMaxLength(80).IsRequired();
        outbox.Property(value => value.PayloadJson).IsRequired();
        outbox.Property(value => value.LastError).HasMaxLength(300);
        outbox.HasIndex(value => new { value.AggregateId, value.EventType }).IsUnique();
        outbox.HasIndex(value => new { value.NextAttemptAtUtc, value.CreatedAtUtc }).HasFilter("[PublishedAtUtc] IS NULL");
        outbox.HasOne<Order>().WithMany().HasForeignKey(value => value.AggregateId).OnDelete(DeleteBehavior.Restrict);
    }
}
