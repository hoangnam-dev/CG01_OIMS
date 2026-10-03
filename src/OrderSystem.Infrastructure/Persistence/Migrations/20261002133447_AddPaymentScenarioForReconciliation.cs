using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentScenarioForReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scenario",
                table: "payments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_scenario",
                table: "payments",
                sql: "scenario IS NULL OR scenario IN ('SUCCESS', 'FAILED', 'SUCCESS_BUT_RESPONSE_LOST', 'DELAYED_SUCCESS')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_scenario",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "scenario",
                table: "payments");
        }
    }
}
