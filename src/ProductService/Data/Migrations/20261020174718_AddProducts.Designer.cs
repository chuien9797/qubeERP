using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProductService.Data;

#nullable disable

namespace ProductService.Data.Migrations
{
    [DbContext(typeof(ProductServiceContext))]
    [Migration("20261020174718_AddProducts")]
    partial class AddProducts
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.13")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

            modelBuilder.Entity("ProductService.Domain.Product", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uniqueidentifier")
                        .HasDefaultValueSql("NEWID()");

                    b.Property<DateTimeOffset>("CreatedAtUtc")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("datetimeoffset")
                        .HasDefaultValueSql("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");

                    b.Property<string>("Description")
                        .HasColumnType("nvarchar(max)");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bit")
                        .HasDefaultValue(true);

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("nvarchar(200)");

                    b.Property<string>("Sku")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("nvarchar(50)");

                    b.Property<decimal>("UnitPrice")
                        .HasPrecision(18, 2)
                        .HasColumnType("decimal(18,2)");

                    b.Property<DateTimeOffset>("UpdatedAtUtc")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("datetimeoffset")
                        .HasDefaultValueSql("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");

                    b.HasKey("Id");

                    b.HasIndex("Sku")
                        .IsUnique();

                    b.ToTable("Products", null, t =>
                        {
                            t.HasCheckConstraint("CK_Products_Name", "LEN(LTRIM(RTRIM([Name]))) > 0");

                            t.HasCheckConstraint("CK_Products_Sku", "LEN(LTRIM(RTRIM([Sku]))) > 0");

                            t.HasCheckConstraint("CK_Products_UnitPrice", "[UnitPrice] >= 0");
                        });
                });
#pragma warning restore 612, 618
        }
    }
}
