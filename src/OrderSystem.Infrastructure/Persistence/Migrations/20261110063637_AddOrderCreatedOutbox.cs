using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration composite-index column arrays

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCreatedOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_version = table.Column<short>(type: "smallint", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exchange_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    routing_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.message_id);
                    table.CheckConstraint("ck_outbox_messages_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_outbox_messages_event_type", "length(btrim(event_type)) > 0");
                    table.CheckConstraint("ck_outbox_messages_event_version", "event_version > 0");
                    table.CheckConstraint("ck_outbox_messages_exchange_name", "length(btrim(exchange_name)) > 0");
                    table.CheckConstraint("ck_outbox_messages_failure_category", "last_failure_category IS NULL OR last_failure_category IN ('BrokerUnavailable', 'ConfirmTimeout', 'NegativeConfirmation', 'OutcomeUnknown', 'Unroutable', 'UnsupportedStoredContract')");
                    table.CheckConstraint("ck_outbox_messages_lifecycle", "(status = 'Pending' AND next_attempt_at IS NOT NULL AND claim_token IS NULL AND lease_expires_at IS NULL AND published_at IS NULL) OR (status = 'Publishing' AND next_attempt_at IS NULL AND claim_token IS NOT NULL AND lease_expires_at IS NOT NULL AND published_at IS NULL) OR (status = 'Published' AND next_attempt_at IS NULL AND claim_token IS NULL AND lease_expires_at IS NULL AND published_at IS NOT NULL) OR (status = 'NeedsAttention' AND next_attempt_at IS NULL AND claim_token IS NULL AND lease_expires_at IS NULL AND published_at IS NULL)");
                    table.CheckConstraint("ck_outbox_messages_payload", "jsonb_typeof(payload) = 'object'");
                    table.CheckConstraint("ck_outbox_messages_routing_key", "length(btrim(routing_key)) > 0");
                    table.CheckConstraint("ck_outbox_messages_status", "status IN ('Pending', 'Publishing', 'Published', 'NeedsAttention')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                columns: new[] { "next_attempt_at", "message_id" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_published",
                table: "outbox_messages",
                columns: new[] { "published_at", "message_id" },
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_publishing",
                table: "outbox_messages",
                columns: new[] { "lease_expires_at", "message_id" },
                filter: "status = 'Publishing'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}
