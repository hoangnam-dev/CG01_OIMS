using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration composite-index column arrays

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulatedPaymentRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fake_provider");

            migrationBuilder.CreateTable(
                name: "operations",
                schema: "fake_provider",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_resource_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    parent_provider_payment_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    scenario = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fake_provider_operations", x => x.id);
                    table.CheckConstraint("ck_fake_provider_operations_amount_non_negative", "amount >= 0");
                    table.CheckConstraint("ck_fake_provider_operations_available_after_created", "available_at IS NULL OR available_at >= created_at");
                    table.CheckConstraint("ck_fake_provider_operations_parent_identity", "(operation_type = 'CreatePayment' AND parent_provider_payment_id IS NULL) OR (operation_type = 'RefundPayment' AND parent_provider_payment_id IS NOT NULL AND parent_provider_payment_id = btrim(parent_provider_payment_id) AND length(parent_provider_payment_id) BETWEEN 1 AND 128)");
                    table.CheckConstraint("ck_fake_provider_operations_scenario", "scenario IN ('SUCCESS', 'FAILED', 'SUCCESS_BUT_RESPONSE_LOST', 'DELAYED_SUCCESS')");
                    table.CheckConstraint("ck_fake_provider_operations_status", "status IN ('Pending', 'Processing', 'Succeeded', 'Failed')");
                    table.CheckConstraint("ck_fake_provider_operations_type", "operation_type IN ('CreatePayment', 'RefundPayment')");
                    table.CheckConstraint("ck_fake_provider_operations_updated_after_created", "updated_at >= created_at");
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    gateway_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    refund_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    provider_refund_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_status_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refund_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refund_attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_refund_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    manual_review_required_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount_non_negative", "amount >= 0");
                    table.CheckConstraint("ck_payments_failure_code_lifecycle", "(status = 'Failed' AND failure_code IS NOT NULL AND failure_code = btrim(failure_code) AND length(failure_code) BETWEEN 1 AND 64) OR (status <> 'Failed' AND failure_code IS NULL)");
                    table.CheckConstraint("ck_payments_refund_attempt_count_non_negative", "refund_attempt_count >= 0");
                    table.CheckConstraint("ck_payments_refund_lifecycle", "(status IN ('Pending', 'Processing', 'Succeeded', 'Failed') AND refund_idempotency_key IS NULL AND provider_refund_id IS NULL AND refund_requested_at IS NULL AND refund_attempt_count = 0 AND next_refund_attempt_at IS NULL AND manual_review_required_at IS NULL AND refunded_at IS NULL) OR (status = 'RefundPending' AND refund_idempotency_key IS NOT NULL AND refund_idempotency_key = btrim(refund_idempotency_key) AND length(refund_idempotency_key) BETWEEN 1 AND 128 AND provider_refund_id IS NULL AND refund_requested_at IS NOT NULL AND refunded_at IS NULL AND ((manual_review_required_at IS NULL AND next_refund_attempt_at IS NOT NULL) OR (manual_review_required_at IS NOT NULL AND next_refund_attempt_at IS NULL))) OR (status = 'Refunded' AND refund_idempotency_key IS NOT NULL AND refund_idempotency_key = btrim(refund_idempotency_key) AND length(refund_idempotency_key) BETWEEN 1 AND 128 AND provider_refund_id IS NOT NULL AND provider_refund_id = btrim(provider_refund_id) AND length(provider_refund_id) BETWEEN 1 AND 128 AND refund_requested_at IS NOT NULL AND refunded_at IS NOT NULL AND refunded_at >= refund_requested_at AND next_refund_attempt_at IS NULL AND manual_review_required_at IS NULL)");
                    table.CheckConstraint("ck_payments_status", "status IN ('Pending', 'Processing', 'Succeeded', 'Failed', 'RefundPending', 'Refunded')");
                    table.ForeignKey(
                        name: "fk_payments_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_payment_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_payment_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_provider_payment_events_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_fake_provider_operations_type_key",
                schema: "fake_provider",
                table: "operations",
                columns: new[] { "operation_type", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_fake_provider_operations_type_resource",
                schema: "fake_provider",
                table: "operations",
                columns: new[] { "operation_type", "provider_resource_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_refund_pending",
                table: "payments",
                columns: new[] { "next_refund_attempt_at", "id" },
                filter: "status = 'RefundPending' AND manual_review_required_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_unresolved",
                table: "payments",
                columns: new[] { "last_status_checked_at", "created_at", "id" },
                filter: "status IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "uq_payments_gateway_idempotency_key",
                table: "payments",
                column: "gateway_idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payments_order_id",
                table: "payments",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payments_provider_payment_id",
                table: "payments",
                column: "provider_payment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payments_provider_refund_id",
                table: "payments",
                column: "provider_refund_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payments_refund_idempotency_key",
                table: "payments",
                column: "refund_idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_payment_events_payment_id",
                table: "provider_payment_events",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_provider_payment_events_provider_payment_occurred_id",
                table: "provider_payment_events",
                columns: new[] { "provider", "provider_payment_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "uq_provider_payment_events_provider_event",
                table: "provider_payment_events",
                columns: new[] { "provider", "provider_event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operations",
                schema: "fake_provider");

            migrationBuilder.DropTable(
                name: "provider_payment_events");

            migrationBuilder.DropTable(
                name: "payments");
        }
    }
#pragma warning restore CA1861
}
