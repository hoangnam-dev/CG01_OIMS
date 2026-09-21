using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline column arrays for composite indexes.

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand_quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    reserved_quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventories", x => x.id);
                    table.CheckConstraint("ck_inventories_on_hand_quantity_non_negative", "on_hand_quantity >= 0");
                    table.CheckConstraint("ck_inventories_reserved_not_greater_than_on_hand", "reserved_quantity <= on_hand_quantity");
                    table.CheckConstraint("ck_inventories_reserved_quantity_non_negative", "reserved_quantity >= 0");
                    table.ForeignKey(
                        name: "fk_inventories_product_variants_product_variant_id",
                        column: x => x.product_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    on_hand_delta = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    reserved_delta = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    reference_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_transactions", x => x.id);
                    table.CheckConstraint("ck_inventory_transactions_adjustment_reason", "type <> 'Adjustment' OR (reason IS NOT NULL AND length(btrim(reason)) > 0)");
                    table.CheckConstraint("ck_inventory_transactions_delta_shape", "(type = 'Receipt' AND on_hand_delta > 0 AND reserved_delta = 0) OR (type = 'Reserve' AND on_hand_delta = 0 AND reserved_delta > 0) OR (type = 'Release' AND on_hand_delta = 0 AND reserved_delta < 0) OR (type = 'Issue' AND on_hand_delta < 0 AND reserved_delta = on_hand_delta) OR (type = 'Adjustment' AND on_hand_delta <> 0 AND reserved_delta = 0)");
                    table.CheckConstraint("ck_inventory_transactions_non_zero_delta", "on_hand_delta <> 0 OR reserved_delta <> 0");
                    table.CheckConstraint("ck_inventory_transactions_order_reference", "type NOT IN ('Reserve', 'Release') OR (reference_type = 'Order' AND reference_id IS NOT NULL)");
                    table.CheckConstraint("ck_inventory_transactions_reference_pair", "(reference_type IS NULL AND reference_id IS NULL) OR (reference_type IS NOT NULL AND reference_id IS NOT NULL)");
                    table.CheckConstraint("ck_inventory_transactions_reference_type", "reference_type IS NULL OR reference_type IN ('Order', 'GoodsReceipt', 'Shipment', 'InventoryAdjustment')");
                    table.CheckConstraint("ck_inventory_transactions_type", "type IN ('Receipt', 'Reserve', 'Release', 'Issue', 'Adjustment')");
                    table.ForeignKey(
                        name: "fk_inventory_transactions_product_variants_product_variant_id",
                        column: x => x.product_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_inventories_product_variant_id",
                table: "inventories",
                column: "product_variant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inventory_transactions_reference_created",
                table: "inventory_transactions",
                columns: new[] { "reference_type", "reference_id", "created_at", "id" },
                filter: "reference_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_transactions_variant_created_id",
                table: "inventory_transactions",
                columns: new[] { "product_variant_id", "created_at", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventories");

            migrationBuilder.DropTable(
                name: "inventory_transactions");
        }
    }
}
#pragma warning restore CA1861
