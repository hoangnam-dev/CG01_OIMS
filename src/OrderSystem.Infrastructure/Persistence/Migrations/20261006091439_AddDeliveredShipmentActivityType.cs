using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveredShipmentActivityType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_shipment_activity_histories_activity_type",
                table: "shipment_activity_histories");

            migrationBuilder.AddCheckConstraint(
                name: "ck_shipment_activity_histories_activity_type",
                table: "shipment_activity_histories",
                sql: "activity_type IN ('Created', 'PickingStarted', 'Packed', 'Shipped', 'OutForDeliveryStarted', 'Delivered', 'DeliveryFailed', 'ReturnStarted', 'Returned', 'Restocked')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_shipment_activity_histories_activity_type",
                table: "shipment_activity_histories");

            migrationBuilder.AddCheckConstraint(
                name: "ck_shipment_activity_histories_activity_type",
                table: "shipment_activity_histories",
                sql: "activity_type IN ('Created', 'PickingStarted', 'Packed', 'Shipped', 'OutForDeliveryStarted', 'DeliveryFailed', 'ReturnStarted', 'Returned', 'Restocked')");
        }
    }
}
