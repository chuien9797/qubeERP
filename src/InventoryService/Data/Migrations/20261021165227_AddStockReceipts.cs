using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Data.Migrations
{
    public partial class AddStockReceipts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements");

            migrationBuilder.CreateIndex(
                name: "UX_StockMovements_ReceiptOperation",
                table: "StockMovements",
                column: "OperationId",
                unique: true,
                filter: "[MovementType] = 'StockReceipt'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Receipt",
                table: "StockMovements",
                sql: "[MovementType] <> 'StockReceipt' OR ([OnHandDelta] > 0 AND [ReservedDelta] = 0 AND [Reason] IS NOT NULL AND LEN(LTRIM(RTRIM([Reason]))) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements",
                sql: "[MovementType] IN ('InitialStock', 'StockSet', 'Reserve', 'StockReceipt')");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_StockMovements_ReceiptOperation",
                table: "StockMovements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Receipt",
                table: "StockMovements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements",
                sql: "[MovementType] IN ('InitialStock', 'StockSet', 'Reserve')");
        }
    }
}
