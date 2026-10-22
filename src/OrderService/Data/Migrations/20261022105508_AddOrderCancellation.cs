using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_AggregateId",
                table: "OutboxMessages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OutboxMessages_Event",
                table: "OutboxMessages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_Outcome",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_Status",
                table: "Orders");

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "Orders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_AggregateId_EventType",
                table: "OutboxMessages",
                columns: new[] { "AggregateId", "EventType" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutboxMessages_Event",
                table: "OutboxMessages",
                sql: "[EventType] IN ('OrderConfirmed', 'OrderRejected', 'OrderCancelled') AND [EventVersion] = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_CancellationReason",
                table: "Orders",
                sql: "[Status] NOT IN ('CancellationPending', 'Cancelled') OR ([CancellationReason] IS NOT NULL AND LEN(LTRIM(RTRIM([CancellationReason]))) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_Outcome",
                table: "Orders",
                sql: "([Status] IN ('Pending', 'Confirmed', 'CancellationPending', 'Cancelled', 'Fulfilled') AND [FailureCode] IS NULL AND [FailureDetailsJson] IS NULL)\nOR ([Status] = 'Rejected' AND [FailureCode] IS NOT NULL AND\n    (([FailureCode] = 'INSUFFICIENT_STOCK' AND [FailureDetailsJson] IS NOT NULL)\n    OR ([FailureCode] = 'INVENTORY_INVALID_REQUEST' AND [FailureDetailsJson] IS NULL)))\nOR ([Status] = 'RequiresReview' AND [FailureCode] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_Status",
                table: "Orders",
                sql: "[Status] IN ('Pending', 'Confirmed', 'Rejected', 'RequiresReview', 'CancellationPending', 'Cancelled', 'Fulfilled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_AggregateId_EventType",
                table: "OutboxMessages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OutboxMessages_Event",
                table: "OutboxMessages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_CancellationReason",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_Outcome",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_Status",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "Orders");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_AggregateId",
                table: "OutboxMessages",
                column: "AggregateId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutboxMessages_Event",
                table: "OutboxMessages",
                sql: "[EventType] IN ('OrderConfirmed', 'OrderRejected') AND [EventVersion] = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_Outcome",
                table: "Orders",
                sql: "([Status] IN ('Pending', 'Confirmed') AND [FailureCode] IS NULL AND [FailureDetailsJson] IS NULL)\nOR ([Status] = 'Rejected' AND [FailureCode] IS NOT NULL AND\n    (([FailureCode] = 'INSUFFICIENT_STOCK' AND [FailureDetailsJson] IS NOT NULL)\n    OR ([FailureCode] = 'INVENTORY_INVALID_REQUEST' AND [FailureDetailsJson] IS NULL)))\nOR ([Status] = 'RequiresReview' AND [FailureCode] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_Status",
                table: "Orders",
                sql: "[Status] IN ('Pending', 'Confirmed', 'Rejected', 'RequiresReview')");
        }
    }
}
