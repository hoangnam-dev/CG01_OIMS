using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderStatusHistory : Migration
    {
        private static readonly string[] ActorOccurredIndexColumns = ["actor_user_id", "occurred_at", "id"];
        private static readonly bool[] ActorOccurredIndexDescending = [false, true, true];
        private static readonly string[] OrderOccurredIndexColumns = ["order_id", "occurred_at", "id"];
        private static readonly bool[] OrderOccurredIndexDescending = [false, true, true];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_status_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_status_history", x => x.id);
                    table.CheckConstraint("ck_order_status_history_actor_type", "actor_type IN ('Customer', 'Admin', 'System')");
                    table.CheckConstraint("ck_order_status_history_actor_user", "(actor_type = 'System' AND actor_user_id IS NULL) OR (actor_type IN ('Customer', 'Admin') AND actor_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_order_status_history_admin_cancel_reason", "NOT (actor_type = 'Admin' AND to_status = 'Cancelled') OR (reason IS NOT NULL AND length(btrim(reason)) > 0)");
                    table.CheckConstraint("ck_order_status_history_reason_code", "reason_code IN ('CustomerRequested', 'CustomerSupport', 'FraudSuspected', 'DuplicateOrder', 'InventoryIssue', 'PolicyViolation', 'Other')");
                    table.CheckConstraint("ck_order_status_history_reason_format", "reason IS NULL OR (reason = btrim(reason) AND length(reason) <= 500)");
                    table.CheckConstraint("ck_order_status_history_status_transition", "from_status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired') AND to_status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired') AND from_status <> to_status");
                    table.ForeignKey(
                        name: "fk_order_status_history_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_status_history_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_actor_occurred_id",
                table: "order_status_history",
                columns: ActorOccurredIndexColumns,
                descending: ActorOccurredIndexDescending,
                filter: "actor_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_order_occurred_id",
                table: "order_status_history",
                columns: OrderOccurredIndexColumns,
                descending: OrderOccurredIndexDescending);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_status_history");
        }
    }
}
