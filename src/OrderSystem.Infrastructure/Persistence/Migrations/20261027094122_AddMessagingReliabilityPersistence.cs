using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration composite-index column arrays

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingReliabilityPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "failed_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    raw_message_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    event_version = table.Column<int>(type: "integer", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    body = table.Column<byte[]>(type: "bytea", nullable: false),
                    body_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    safe_headers = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    original_exchange = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    original_routing_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    failure_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Pending"),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    redrive_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    redrive_requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    redrive_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    redriven_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    terminal_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_failed_messages", x => x.id);
                    table.CheckConstraint("ck_failed_messages_attempt_count", "attempt_count BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_failed_messages_body_sha256_format", "body_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_failed_messages_close_reason_format", "close_reason IS NULL OR (close_reason = btrim(close_reason) AND length(close_reason) BETWEEN 1 AND 500)");
                    table.CheckConstraint("ck_failed_messages_consumer_name_not_blank", "length(btrim(consumer_name)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_failed_messages_event_type_not_blank", "event_type IS NULL OR length(btrim(event_type)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_failed_messages_failure_code_not_blank", "length(btrim(failure_code)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_failed_messages_failure_kind", "failure_kind IN ('Malformed', 'UnsupportedVersion', 'Permanent', 'RetryExhausted', 'Expired')");
                    table.CheckConstraint("ck_failed_messages_lifecycle", "(status = 'Pending' AND redrive_requested_at IS NULL AND redrive_requested_by_user_id IS NULL AND redrive_reason IS NULL AND redriven_at IS NULL AND closed_at IS NULL AND closed_by_user_id IS NULL AND close_reason IS NULL AND terminal_at IS NULL) OR (status = 'RedriveRequested' AND redrive_requested_at IS NOT NULL AND redrive_requested_by_user_id IS NOT NULL AND redrive_reason IS NOT NULL AND redriven_at IS NULL AND closed_at IS NULL AND closed_by_user_id IS NULL AND close_reason IS NULL AND terminal_at IS NULL) OR (status = 'Redriven' AND redrive_requested_at IS NOT NULL AND redrive_requested_by_user_id IS NOT NULL AND redrive_reason IS NOT NULL AND redriven_at IS NOT NULL AND closed_at IS NULL AND closed_by_user_id IS NULL AND close_reason IS NULL AND terminal_at IS NOT NULL) OR (status = 'Closed' AND redrive_requested_at IS NULL AND redrive_requested_by_user_id IS NULL AND redrive_reason IS NULL AND redriven_at IS NULL AND closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL AND close_reason IS NOT NULL AND terminal_at IS NOT NULL) OR (status = 'Expired' AND ((redrive_requested_at IS NULL AND redrive_requested_by_user_id IS NULL AND redrive_reason IS NULL) OR (redrive_requested_at IS NOT NULL AND redrive_requested_by_user_id IS NOT NULL AND redrive_reason IS NOT NULL)) AND redriven_at IS NULL AND closed_at IS NULL AND closed_by_user_id IS NULL AND close_reason IS NULL AND terminal_at IS NOT NULL)");
                    table.CheckConstraint("ck_failed_messages_original_exchange_not_blank", "length(btrim(original_exchange)) BETWEEN 1 AND 255");
                    table.CheckConstraint("ck_failed_messages_original_routing_key_not_blank", "length(btrim(original_routing_key)) BETWEEN 1 AND 255");
                    table.CheckConstraint("ck_failed_messages_raw_message_id_not_blank", "raw_message_id IS NULL OR length(btrim(raw_message_id)) BETWEEN 1 AND 255");
                    table.CheckConstraint("ck_failed_messages_redrive_reason_format", "redrive_reason IS NULL OR (redrive_reason = btrim(redrive_reason) AND length(redrive_reason) BETWEEN 1 AND 500)");
                    table.CheckConstraint("ck_failed_messages_status", "status IN ('Pending', 'RedriveRequested', 'Redriven', 'Closed', 'Expired')");
                    table.ForeignKey(
                        name: "fk_failed_messages_users_closed_by_user_id",
                        column: x => x.closed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_failed_messages_users_redrive_requested_by_user_id",
                        column: x => x.redrive_requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_messages", x => new { x.message_id, x.consumer_name });
                    table.CheckConstraint("ck_processed_messages_consumer_name_not_blank", "length(btrim(consumer_name)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "failed_message_payload_accesses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    failed_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accessed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    accessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_failed_message_payload_accesses", x => x.id);
                    table.CheckConstraint("ck_failed_message_payload_accesses_reason", "length(btrim(reason)) BETWEEN 1 AND 500");
                    table.ForeignKey(
                        name: "fk_failed_message_payload_accesses_failed_message",
                        column: x => x.failed_message_id,
                        principalTable: "failed_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_failed_message_payload_accesses_user",
                        column: x => x.accessed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_failed_message_payload_accesses_accessed_by_user_id",
                table: "failed_message_payload_accesses",
                column: "accessed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_failed_message_payload_accesses_failure_time",
                table: "failed_message_payload_accesses",
                columns: new[] { "failed_message_id", "accessed_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_failed_messages_body_sha256",
                table: "failed_messages",
                column: "body_sha256");

            migrationBuilder.CreateIndex(
                name: "IX_failed_messages_closed_by_user_id",
                table: "failed_messages",
                column: "closed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_failed_messages_message_consumer",
                table: "failed_messages",
                columns: new[] { "message_id", "consumer_name" });

            migrationBuilder.CreateIndex(
                name: "IX_failed_messages_redrive_requested_by_user_id",
                table: "failed_messages",
                column: "redrive_requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_failed_messages_status_captured_id",
                table: "failed_messages",
                columns: new[] { "status", "captured_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_failed_messages_terminal_id",
                table: "failed_messages",
                columns: new[] { "terminal_at", "id" },
                filter: "terminal_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_processed_messages_processed_at",
                table: "processed_messages",
                columns: new[] { "processed_at", "message_id", "consumer_name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "failed_message_payload_accesses");

            migrationBuilder.DropTable(
                name: "processed_messages");

            migrationBuilder.DropTable(
                name: "failed_messages");
        }
    }
}
