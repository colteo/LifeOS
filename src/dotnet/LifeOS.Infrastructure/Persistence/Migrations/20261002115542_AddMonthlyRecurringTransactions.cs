using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyRecurringTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_transactions_id_user_id",
                table: "transactions",
                columns: new[] { "id", "user_id" });

            migrationBuilder.CreateTable(
                name: "recurring_transaction_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    transaction_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    day_of_month = table.Column<int>(type: "integer", nullable: false),
                    start_year = table.Column<int>(type: "integer", nullable: false),
                    start_month = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_transaction_rules", x => x.id);
                    table.UniqueConstraint("AK_recurring_transaction_rules_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_recurring_rules_amount", "amount > 0");
                    table.CheckConstraint("ck_recurring_rules_day", "day_of_month BETWEEN 1 AND 31");
                    table.CheckConstraint("ck_recurring_rules_name", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_recurring_rules_start", "start_year BETWEEN 1 AND 9998 AND start_month BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_recurring_rules_type", "transaction_type IN ('Income', 'Expense')");
                    table.ForeignKey(
                        name: "FK_recurring_transaction_rules_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_rules_account_owner",
                        columns: x => new { x.account_id, x.user_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_rules_category_owner",
                        columns: x => new { x.category_id, x.user_id },
                        principalTable: "categories",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recurring_transaction_occurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recurring_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    scheduled_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_transaction_occurrences", x => x.id);
                    table.CheckConstraint("ck_recurring_occurrences_date", "EXTRACT(YEAR FROM scheduled_date) = year AND EXTRACT(MONTH FROM scheduled_date) = month");
                    table.CheckConstraint("ck_recurring_occurrences_month", "year BETWEEN 1 AND 9998 AND month BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_recurring_occurrences_shape", "(status = 'Confirmed' AND transaction_id IS NOT NULL) OR (status = 'Skipped' AND transaction_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_recurring_transaction_occurrences_recurring_transaction_rul~",
                        columns: x => new { x.recurring_rule_id, x.user_id },
                        principalTable: "recurring_transaction_rules",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recurring_transaction_occurrences_transactions_transaction_~",
                        columns: x => new { x.transaction_id, x.user_id },
                        principalTable: "transactions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_transaction_occurrences_recurring_rule_id_user_id",
                table: "recurring_transaction_occurrences",
                columns: new[] { "recurring_rule_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_transaction_occurrences_transaction_id_user_id",
                table: "recurring_transaction_occurrences",
                columns: new[] { "transaction_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_recurring_occurrences_rule_month",
                table: "recurring_transaction_occurrences",
                columns: new[] { "recurring_rule_id", "year", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recurring_transaction_rules_account_id_user_id",
                table: "recurring_transaction_rules",
                columns: new[] { "account_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_transaction_rules_category_id_user_id",
                table: "recurring_transaction_rules",
                columns: new[] { "category_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_transaction_rules_user_id",
                table: "recurring_transaction_rules",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recurring_transaction_occurrences");

            migrationBuilder.DropTable(
                name: "recurring_transaction_rules");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_transactions_id_user_id",
                table: "transactions");
        }
    }
}
