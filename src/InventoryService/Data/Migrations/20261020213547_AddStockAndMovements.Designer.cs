using System;
using InventoryService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace InventoryService.Data.Migrations
{
    [DbContext(typeof(InventoryServiceContext))]
    [Migration("20261020213547_AddStockAndMovements")]
    partial class AddStockAndMovements
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.13")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

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
#pragma warning restore 612, 618
        }
    }
}
