using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFulfillmentStatusHistoryCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_status",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status_history_reason_code",
                table: "order_status_history");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status_history_status_transition",
                table: "order_status_history");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_status",
                table: "orders",
                sql: "status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'FulfillmentFailed', 'Cancelled', 'Expired')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status_history_reason_code",
                table: "order_status_history",
                sql: "reason_code IN ('CustomerRequested', 'CustomerSupport', 'FraudSuspected', 'DuplicateOrder', 'InventoryIssue', 'PolicyViolation', 'Other', 'ReservationExpired', 'ShipmentCreated', 'ShipmentDelivered', 'ShipmentReturned')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status_history_status_transition",
                table: "order_status_history",
                sql: "from_status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'FulfillmentFailed', 'Cancelled', 'Expired') AND to_status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'FulfillmentFailed', 'Cancelled', 'Expired') AND from_status <> to_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_status",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status_history_reason_code",
                table: "order_status_history");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status_history_status_transition",
                table: "order_status_history");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_status",
                table: "orders",
                sql: "status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status_history_reason_code",
                table: "order_status_history",
                sql: "reason_code IN ('CustomerRequested', 'CustomerSupport', 'FraudSuspected', 'DuplicateOrder', 'InventoryIssue', 'PolicyViolation', 'Other', 'ReservationExpired')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status_history_status_transition",
                table: "order_status_history",
                sql: "from_status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired') AND to_status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired') AND from_status <> to_status");
        }
    }
}
