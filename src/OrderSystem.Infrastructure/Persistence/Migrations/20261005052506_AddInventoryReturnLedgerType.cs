using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryReturnLedgerType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_transactions_delta_shape",
                table: "inventory_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_transactions_type",
                table: "inventory_transactions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_transactions_delta_shape",
                table: "inventory_transactions",
                sql: "(type = 'Receipt' AND on_hand_delta > 0 AND reserved_delta = 0) OR (type = 'Reserve' AND on_hand_delta = 0 AND reserved_delta > 0) OR (type = 'Release' AND on_hand_delta = 0 AND reserved_delta < 0) OR (type = 'Issue' AND on_hand_delta < 0 AND reserved_delta = on_hand_delta) OR (type = 'Return' AND on_hand_delta > 0 AND reserved_delta = 0) OR (type = 'Adjustment' AND on_hand_delta <> 0 AND reserved_delta = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_transactions_return_reference",
                table: "inventory_transactions",
                sql: "type <> 'Return' OR (reference_type = 'Shipment' AND reference_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_transactions_type",
                table: "inventory_transactions",
                sql: "type IN ('Receipt', 'Reserve', 'Release', 'Issue', 'Return', 'Adjustment')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_transactions_delta_shape",
                table: "inventory_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_transactions_return_reference",
                table: "inventory_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_transactions_type",
                table: "inventory_transactions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_transactions_delta_shape",
                table: "inventory_transactions",
                sql: "(type = 'Receipt' AND on_hand_delta > 0 AND reserved_delta = 0) OR (type = 'Reserve' AND on_hand_delta = 0 AND reserved_delta > 0) OR (type = 'Release' AND on_hand_delta = 0 AND reserved_delta < 0) OR (type = 'Issue' AND on_hand_delta < 0 AND reserved_delta = on_hand_delta) OR (type = 'Adjustment' AND on_hand_delta <> 0 AND reserved_delta = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_transactions_type",
                table: "inventory_transactions",
                sql: "type IN ('Receipt', 'Reserve', 'Release', 'Issue', 'Adjustment')");
        }
    }
}
