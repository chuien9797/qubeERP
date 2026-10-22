using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reservations_Outcome",
                table: "Reservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reservations_Status",
                table: "Reservations");

            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "StockMovements",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Release",
                table: "StockMovements",
                sql: "[MovementType] <> 'Release' OR ([OnHandDelta] = 0 AND [ReservedDelta] < 0 AND [Reason] IS NOT NULL AND LEN(LTRIM(RTRIM([Reason]))) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements",
                sql: "[MovementType] IN ('InitialStock', 'StockSet', 'Reserve', 'StockReceipt', 'Release')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reservations_Outcome",
                table: "Reservations",
                sql: "([Status] = 'Rejected' AND [FailureCode] IS NOT NULL\n    AND [FailureCode] = 'INSUFFICIENT_STOCK' AND [FailureDetailsJson] IS NOT NULL)\nOR ([Status] IN ('Processing', 'Reserved', 'Released', 'Consumed') AND [FailureCode] IS NULL AND [FailureDetailsJson] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reservations_Status",
                table: "Reservations",
                sql: "[Status] IN ('Processing', 'Reserved', 'Rejected', 'Released', 'Consumed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Release",
                table: "StockMovements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reservations_Outcome",
                table: "Reservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reservations_Status",
                table: "Reservations");

            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "StockMovements",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements",
                sql: "[MovementType] IN ('InitialStock', 'StockSet', 'Reserve', 'StockReceipt')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reservations_Outcome",
                table: "Reservations",
                sql: "([Status] = 'Rejected' AND [FailureCode] IS NOT NULL\n    AND [FailureCode] = 'INSUFFICIENT_STOCK' AND [FailureDetailsJson] IS NOT NULL)\nOR ([Status] IN ('Processing', 'Reserved') AND [FailureCode] IS NULL AND [FailureDetailsJson] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reservations_Status",
                table: "Reservations",
                sql: "[Status] IN ('Processing', 'Reserved', 'Rejected')");
        }
    }
}
