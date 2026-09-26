using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline column arrays for composite indexes.

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreateOrderIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Processing"),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    http_status_code = table.Column<short>(type: "smallint", nullable: true),
                    response_body_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delete_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_requests", x => x.id);
                    table.CheckConstraint("ck_idempotency_requests_completion_time", "completed_at IS NULL OR completed_at >= created_at");
                    table.CheckConstraint("ck_idempotency_requests_create_order_completed", "operation <> 'CreateOrder'\r\nOR status <> 'Completed'\r\nOR (resource_id IS NOT NULL AND http_status_code = 201)");
                    table.CheckConstraint("ck_idempotency_requests_http_status_code", "http_status_code IS NULL OR http_status_code BETWEEN 100 AND 599");
                    table.CheckConstraint("ck_idempotency_requests_lifecycle", "(status = 'Processing'\r\n    AND resource_id IS NULL\r\n    AND http_status_code IS NULL\r\n    AND response_body_json IS NULL\r\n    AND completed_at IS NULL)\r\nOR\r\n(status = 'Completed'\r\n    AND http_status_code BETWEEN 200 AND 299\r\n    AND response_body_json IS NOT NULL\r\n    AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_idempotency_requests_operation", "operation IN ('CreateOrder')");
                    table.CheckConstraint("ck_idempotency_requests_request_hash_length", "octet_length(request_hash) = 32");
                    table.CheckConstraint("ck_idempotency_requests_response_body_size", "response_body_json IS NULL OR octet_length(response_body_json) <= 65536");
                    table.CheckConstraint("ck_idempotency_requests_retention", "expires_at > created_at AND delete_after > expires_at");
                    table.CheckConstraint("ck_idempotency_requests_status", "status IN ('Processing', 'Completed')");
                    table.ForeignKey(
                        name: "fk_idempotency_requests_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_requests_terminal_cleanup",
                table: "idempotency_requests",
                columns: new[] { "delete_after", "id" },
                filter: "status = 'Completed'");

            migrationBuilder.CreateIndex(
                name: "uq_idempotency_requests_user_operation_key",
                table: "idempotency_requests",
                columns: new[] { "user_id", "operation", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_requests");
        }
    }
}
#pragma warning restore CA1861
