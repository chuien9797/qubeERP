using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderService.Data.Migrations
{
    public partial class AddOrders : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequestFingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    FailureDetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Orders_FailureJson", "[FailureDetailsJson] IS NULL OR ISJSON([FailureDetailsJson]) = 1");
                    table.CheckConstraint("CK_Orders_Key", "LEN([IdempotencyKey]) > 0");
                    table.CheckConstraint("CK_Orders_Outcome", "([Status] IN ('Pending', 'Confirmed') AND [FailureCode] IS NULL AND [FailureDetailsJson] IS NULL)\nOR ([Status] = 'Rejected' AND [FailureCode] IS NOT NULL AND\n    (([FailureCode] = 'INSUFFICIENT_STOCK' AND [FailureDetailsJson] IS NOT NULL)\n    OR ([FailureCode] = 'INVENTORY_INVALID_REQUEST' AND [FailureDetailsJson] IS NULL)))\nOR ([Status] = 'RequiresReview' AND [FailureCode] IS NOT NULL)");
                    table.CheckConstraint("CK_Orders_Status", "[Status] IN ('Pending', 'Confirmed', 'Rejected', 'RequiresReview')");
                    table.CheckConstraint("CK_Orders_Total", "[TotalAmount] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "OrderLines",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLines", x => new { x.OrderId, x.ProductId });
                    table.CheckConstraint("CK_OrderLines_Amounts", "[UnitPrice] >= 0 AND [LineTotal] >= 0 AND [LineTotal] = [UnitPrice] * [Quantity]");
                    table.CheckConstraint("CK_OrderLines_Quantity", "[Quantity] >= 1 AND [Quantity] <= 10000");
                    table.ForeignKey(
                        name: "FK_OrderLines_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_IdempotencyKey",
                table: "Orders",
                column: "IdempotencyKey",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderLines");

            migrationBuilder.DropTable(
                name: "Orders");
        }
    }
}
