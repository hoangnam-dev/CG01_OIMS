using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration composite-index column arrays

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShipmentPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    returned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    restocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipments", x => x.id);
                    table.CheckConstraint("ck_shipments_failure_reason_lifecycle", "(status = 'DeliveryFailed' AND failure_reason IS NOT NULL AND failure_reason = btrim(failure_reason) AND length(failure_reason) BETWEEN 1 AND 500) OR (status <> 'DeliveryFailed' AND failure_reason IS NULL)");
                    table.CheckConstraint("ck_shipments_lifecycle_timestamps_after_created", "(shipped_at IS NULL OR shipped_at >= created_at) AND (delivered_at IS NULL OR delivered_at >= created_at) AND (returned_at IS NULL OR returned_at >= created_at) AND (restocked_at IS NULL OR restocked_at >= created_at)");
                    table.CheckConstraint("ck_shipments_status", "status IN ('Pending', 'Picking', 'Packed', 'Shipped', 'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returning', 'Returned')");
                    table.CheckConstraint("ck_shipments_updated_after_created", "updated_at >= created_at");
                    table.ForeignKey(
                        name: "fk_shipments_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_status_updated_id",
                table: "shipments",
                columns: new[] { "status", "updated_at", "id" });

            migrationBuilder.CreateIndex(
                name: "uq_shipments_order_id",
                table: "shipments",
                column: "order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shipments");
        }
    }
#pragma warning restore CA1861
}
