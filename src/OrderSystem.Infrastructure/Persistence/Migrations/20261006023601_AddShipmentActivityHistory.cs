using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration composite-index column arrays

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShipmentActivityHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shipment_activity_histories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activity_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    actor_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment_activity_histories", x => x.id);
                    table.CheckConstraint("ck_shipment_activity_histories_activity_type", "activity_type IN ('Created', 'PickingStarted', 'Packed', 'Shipped', 'OutForDeliveryStarted', 'DeliveryFailed', 'ReturnStarted', 'Returned', 'Restocked')");
                    table.CheckConstraint("ck_shipment_activity_histories_actor_type", "actor_type IN ('Admin', 'System', 'Carrier')");
                    table.CheckConstraint("ck_shipment_activity_histories_actor_user", "(actor_type IN ('System', 'Carrier') AND actor_user_id IS NULL) OR (actor_type = 'Admin' AND actor_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_shipment_activity_histories_from_status", "from_status IS NULL OR from_status IN ('Pending', 'Picking', 'Packed', 'Shipped', 'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returning', 'Returned')");
                    table.CheckConstraint("ck_shipment_activity_histories_reason_format", "reason IS NULL OR (reason = btrim(reason) AND length(reason) <= 500)");
                    table.CheckConstraint("ck_shipment_activity_histories_to_status", "to_status IS NULL OR to_status IN ('Pending', 'Picking', 'Packed', 'Shipped', 'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returning', 'Returned')");
                    table.ForeignKey(
                        name: "fk_shipment_activity_histories_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipment_activity_histories_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shipment_activity_histories_actor_user_id",
                table: "shipment_activity_histories",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_shipment_activity_histories_shipment_occurred_id",
                table: "shipment_activity_histories",
                columns: new[] { "shipment_id", "occurred_at", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shipment_activity_histories");
        }
    }
#pragma warning restore CA1861
}
