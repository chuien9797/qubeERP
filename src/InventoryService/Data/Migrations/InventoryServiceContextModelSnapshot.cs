using System;
using InventoryService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace InventoryService.Data.Migrations
{
    [DbContext(typeof(InventoryServiceContext))]
    partial class InventoryServiceContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.13")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

            modelBuilder.Entity("InventoryService.Domain.Reservation", b =>
                {
                    b.Property<Guid>("Id")
                        .HasColumnType("uniqueidentifier");

                    b.Property<DateTimeOffset>("CreatedAtUtc")
                        .HasColumnType("datetimeoffset");

                    b.Property<string>("FailureCode")
                        .HasMaxLength(80)
                        .HasColumnType("nvarchar(80)");

                    b.Property<string>("FailureDetailsJson")
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("RequestFingerprint")
                        .IsRequired()
                        .HasColumnType("char(64)");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(24)
                        .HasColumnType("nvarchar(24)");

                    b.Property<DateTimeOffset>("UpdatedAtUtc")
                        .HasColumnType("datetimeoffset");

                    b.HasKey("Id");

                    b.ToTable("Reservations", null, t =>
                        {
                            t.HasCheckConstraint("CK_Reservations_FailureJson", "[FailureDetailsJson] IS NULL OR ISJSON([FailureDetailsJson]) = 1");

                            t.HasCheckConstraint("CK_Reservations_Outcome", "([Status] = 'Rejected' AND [FailureCode] IS NOT NULL\n    AND [FailureCode] = 'INSUFFICIENT_STOCK' AND [FailureDetailsJson] IS NOT NULL)\nOR ([Status] IN ('Processing', 'Reserved') AND [FailureCode] IS NULL AND [FailureDetailsJson] IS NULL)");

                            t.HasCheckConstraint("CK_Reservations_Status", "[Status] IN ('Processing', 'Reserved', 'Rejected')");
                        });
                });

            modelBuilder.Entity("InventoryService.Domain.ReservationLine", b =>
                {
                    b.Property<Guid>("ReservationId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<Guid>("ProductId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<int>("Quantity")
                        .HasColumnType("int");

                    b.HasKey("ReservationId", "ProductId");

                    b.ToTable("ReservationLines", null, t =>
                        {
                            t.HasCheckConstraint("CK_ReservationLines_Quantity", "[Quantity] >= 1 AND [Quantity] <= 10000");
                        });
                });

            modelBuilder.Entity("InventoryService.Domain.StockItem", b =>
                {
                    b.Property<Guid>("ProductId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<int>("OnHand")
                        .HasColumnType("int");

                    b.Property<int>("Reserved")
                        .HasColumnType("int");

                    b.Property<DateTimeOffset>("UpdatedAtUtc")
                        .HasColumnType("datetimeoffset");

                    b.HasKey("ProductId");

                    b.ToTable("StockItems", null, t =>
                        {
                            t.HasCheckConstraint("CK_StockItems_Balances", "[OnHand] >= 0 AND [Reserved] >= 0 AND [Reserved] <= [OnHand]");
                        });
                });

            modelBuilder.Entity("InventoryService.Domain.StockMovement", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<long>("Id"));

                    b.Property<DateTimeOffset>("CreatedAtUtc")
                        .HasColumnType("datetimeoffset");

                    b.Property<string>("MovementType")
                        .IsRequired()
                        .HasMaxLength(24)
                        .HasColumnType("nvarchar(24)");

                    b.Property<int>("OnHandAfter")
                        .HasColumnType("int");

                    b.Property<int>("OnHandDelta")
                        .HasColumnType("int");

                    b.Property<Guid>("OperationId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<Guid>("ProductId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<string>("Reason")
                        .HasMaxLength(300)
                        .HasColumnType("nvarchar(300)");

                    b.Property<int>("ReservedAfter")
                        .HasColumnType("int");

                    b.Property<int>("ReservedDelta")
                        .HasColumnType("int");

                    b.HasKey("Id");

                    b.HasIndex("ProductId", "Id");

                    b.HasIndex("OperationId", "ProductId", "MovementType")
                        .IsUnique();

                    b.ToTable("StockMovements", null, t =>
                        {
                            t.HasCheckConstraint("CK_StockMovements_Balances", "[OnHandAfter] >= 0 AND [ReservedAfter] >= 0 AND [ReservedAfter] <= [OnHandAfter]");

                            t.HasCheckConstraint("CK_StockMovements_Type", "[MovementType] IN ('InitialStock', 'StockSet', 'Reserve')");
                        });
                });

            modelBuilder.Entity("InventoryService.Domain.ReservationLine", b =>
                {
                    b.HasOne("InventoryService.Domain.Reservation", null)
                        .WithMany()
                        .HasForeignKey("ReservationId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();
                });
#pragma warning restore 612, 618
        }
    }
}
