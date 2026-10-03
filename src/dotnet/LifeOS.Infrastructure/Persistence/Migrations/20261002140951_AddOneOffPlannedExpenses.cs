using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOneOffPlannedExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planned_expenses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    scheduled_date = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planned_expenses", x => x.id);
                    table.UniqueConstraint("AK_planned_expenses_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_planned_expenses_amount", "expected_amount > 0");
                    table.CheckConstraint("ck_planned_expenses_date", "scheduled_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
                    table.CheckConstraint("ck_planned_expenses_name", "length(btrim(name)) > 0");
                    table.ForeignKey(
                        name: "FK_planned_expenses_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planned_expenses_account_owner",
                        columns: x => new { x.account_id, x.user_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planned_expenses_category_owner",
                        columns: x => new { x.category_id, x.user_id },
                        principalTable: "categories",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "planned_expense_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_expense_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planned_expense_states", x => x.id);
                    table.CheckConstraint("ck_planned_expense_states_shape", "(status = 'Confirmed' AND transaction_id IS NOT NULL) OR (status = 'Cancelled' AND transaction_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_planned_expense_states_planned_expenses_planned_expense_id_~",
                        columns: x => new { x.planned_expense_id, x.user_id },
                        principalTable: "planned_expenses",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_planned_expense_states_transactions_transaction_id_user_id",
                        columns: x => new { x.transaction_id, x.user_id },
                        principalTable: "transactions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_planned_expense_states_planned_expense_id_user_id",
                table: "planned_expense_states",
                columns: new[] { "planned_expense_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_planned_expense_states_transaction_id",
                table: "planned_expense_states",
                column: "transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planned_expense_states_transaction_id_user_id",
                table: "planned_expense_states",
                columns: new[] { "transaction_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_planned_expense_states_item",
                table: "planned_expense_states",
                column: "planned_expense_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planned_expenses_account_id_user_id",
                table: "planned_expenses",
                columns: new[] { "account_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_planned_expenses_category_id_user_id",
                table: "planned_expenses",
                columns: new[] { "category_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_planned_expenses_user_id_scheduled_date",
                table: "planned_expenses",
                columns: new[] { "user_id", "scheduled_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planned_expense_states");

            migrationBuilder.DropTable(
                name: "planned_expenses");
        }
    }
}
