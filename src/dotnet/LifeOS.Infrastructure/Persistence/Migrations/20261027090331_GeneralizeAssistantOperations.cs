using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeAssistantOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_assistant_operations_result_transaction",
                table: "assistant_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assistant_operations_kind",
                table: "assistant_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assistant_operations_result",
                table: "assistant_operations");

            migrationBuilder.RenameColumn(
                name: "result_transaction_id",
                table: "assistant_operations",
                newName: "result_id");

            migrationBuilder.CreateIndex(
                name: "ux_assistant_operations_result",
                table: "assistant_operations",
                column: "result_id",
                unique: true,
                filter: "result_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assistant_operations_kind",
                table: "assistant_operations",
                sql: "kind IN ('finance.transaction.create', 'hydration.intake.create', 'nutrition.meal.create')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assistant_operations_result",
                table: "assistant_operations",
                sql: "(status = 'Executed') = (result_id IS NOT NULL) AND (status = 'Executed') = (executed_at_utc IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_assistant_operations_result",
                table: "assistant_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assistant_operations_kind",
                table: "assistant_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assistant_operations_result",
                table: "assistant_operations");

            migrationBuilder.RenameColumn(
                name: "result_id",
                table: "assistant_operations",
                newName: "result_transaction_id");

            migrationBuilder.CreateIndex(
                name: "ux_assistant_operations_result_transaction",
                table: "assistant_operations",
                column: "result_transaction_id",
                unique: true,
                filter: "result_transaction_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assistant_operations_kind",
                table: "assistant_operations",
                sql: "kind IN ('finance.transaction.create')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assistant_operations_result",
                table: "assistant_operations",
                sql: "(status = 'Executed') = (result_transaction_id IS NOT NULL) AND (status = 'Executed') = (executed_at_utc IS NOT NULL)");
        }
    }
}
