using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActivateInitiatePaymentIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_requests_create_order_completed",
                table: "idempotency_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_requests_lifecycle",
                table: "idempotency_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_requests_operation",
                table: "idempotency_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_requests_create_order_completed",
                table: "idempotency_requests",
                sql: "operation <> 'CreateOrder'\nOR status <> 'Completed'\nOR (\n    resource_id IS NOT NULL\n    AND http_status_code = 201\n    AND response_body_json IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_requests_initiate_payment_completed",
                table: "idempotency_requests",
                sql: "operation <> 'InitiatePayment'\nOR status <> 'Completed'\nOR (\n    resource_id IS NOT NULL\n    AND http_status_code IS NULL\n    AND response_body_json IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_requests_lifecycle",
                table: "idempotency_requests",
                sql: "(status = 'Processing'\r\n    AND resource_id IS NULL\r\n    AND http_status_code IS NULL\r\n    AND response_body_json IS NULL\r\n    AND completed_at IS NULL)\r\nOR\n(status = 'Completed'\n    AND resource_id IS NOT NULL\n    AND completed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_requests_operation",
                table: "idempotency_requests",
                sql: "operation IN ('CreateOrder', 'InitiatePayment')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_requests_create_order_completed",
                table: "idempotency_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_requests_initiate_payment_completed",
                table: "idempotency_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_requests_lifecycle",
                table: "idempotency_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_requests_operation",
                table: "idempotency_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_requests_create_order_completed",
                table: "idempotency_requests",
                sql: "operation <> 'CreateOrder'\r\nOR status <> 'Completed'\r\nOR (resource_id IS NOT NULL AND http_status_code = 201)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_requests_lifecycle",
                table: "idempotency_requests",
                sql: "(status = 'Processing'\r\n    AND resource_id IS NULL\r\n    AND http_status_code IS NULL\r\n    AND response_body_json IS NULL\r\n    AND completed_at IS NULL)\r\nOR\r\n(status = 'Completed'\r\n    AND http_status_code BETWEEN 200 AND 299\r\n    AND response_body_json IS NOT NULL\r\n    AND completed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_requests_operation",
                table: "idempotency_requests",
                sql: "operation IN ('CreateOrder')");
        }
    }
}
