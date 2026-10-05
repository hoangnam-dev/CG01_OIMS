using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitPaymentResponseLossScenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_scenario",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_fake_provider_operations_scenario",
                schema: "fake_provider",
                table: "operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_scenario",
                table: "payments",
                sql: "scenario IS NULL OR scenario IN ('SUCCESS', 'FAILED', 'CLIENT_RESPONSE_LOST', 'PROVIDER_RESPONSE_LOST', 'DELAYED_SUCCESS')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_fake_provider_operations_scenario",
                schema: "fake_provider",
                table: "operations",
                sql: "scenario IN ('SUCCESS', 'FAILED', 'CLIENT_RESPONSE_LOST', 'PROVIDER_RESPONSE_LOST', 'DELAYED_SUCCESS')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_scenario",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_fake_provider_operations_scenario",
                schema: "fake_provider",
                table: "operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_scenario",
                table: "payments",
                sql: "scenario IS NULL OR scenario IN ('SUCCESS', 'FAILED', 'SUCCESS_BUT_RESPONSE_LOST', 'DELAYED_SUCCESS')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_fake_provider_operations_scenario",
                schema: "fake_provider",
                table: "operations",
                sql: "scenario IN ('SUCCESS', 'FAILED', 'SUCCESS_BUT_RESPONSE_LOST', 'DELAYED_SUCCESS')");
        }
    }
}
