using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline column arrays for composite indexes.

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reservation_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_reservation_expires_after_created", "reservation_expires_at > created_at");
                    table.CheckConstraint("ck_orders_status", "status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired')");
                    table.CheckConstraint("ck_orders_total_amount_non_negative", "total_amount >= 0");
                    table.ForeignKey(
                        name: "fk_orders_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => x.id);
                    table.CheckConstraint("ck_order_items_line_total_matches_unit_price", "line_total = quantity * unit_price");
                    table.CheckConstraint("ck_order_items_line_total_non_negative", "line_total >= 0");
                    table.CheckConstraint("ck_order_items_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_order_items_unit_price_non_negative", "unit_price >= 0");
                    table.ForeignKey(
                        name: "fk_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_items_product_variants_product_variant_id",
                        column: x => x.product_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_items_product_variant_id",
                table: "order_items",
                column: "product_variant_id");

            migrationBuilder.CreateIndex(
                name: "uq_order_items_order_variant",
                table: "order_items",
                columns: new[] { "order_id", "product_variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_pending_expiration",
                table: "orders",
                columns: new[] { "reservation_expires_at", "id" },
                filter: "status = 'PendingPayment'");

            migrationBuilder.CreateIndex(
                name: "ix_orders_status_created_id",
                table: "orders",
                columns: new[] { "status", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_created_id",
                table: "orders",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "orders");
        }
    }
}
#pragma warning restore CA1861
