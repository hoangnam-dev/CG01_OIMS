using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationExpiredOrderStatusReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status_history_reason_code",
                table: "order_status_history");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status_history_reason_code",
                table: "order_status_history",
                sql: "reason_code IN ('CustomerRequested', 'CustomerSupport', 'FraudSuspected', 'DuplicateOrder', 'InventoryIssue', 'PolicyViolation', 'Other', 'ReservationExpired')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status_history_reason_code",
                table: "order_status_history");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status_history_reason_code",
                table: "order_status_history",
                sql: "reason_code IN ('CustomerRequested', 'CustomerSupport', 'FraudSuspected', 'DuplicateOrder', 'InventoryIssue', 'PolicyViolation', 'Other')");
        }
    }
}
