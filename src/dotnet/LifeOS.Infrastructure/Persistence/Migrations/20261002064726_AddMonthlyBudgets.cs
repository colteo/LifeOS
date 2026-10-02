using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monthly_budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monthly_budgets", x => x.id);
                    table.CheckConstraint("ck_monthly_budgets_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_monthly_budgets_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_monthly_budgets_year_month", "year BETWEEN 1 AND 9998 AND month BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_monthly_budgets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_monthly_budgets_user_month_currency",
                table: "monthly_budgets",
                columns: new[] { "user_id", "year", "month", "currency" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monthly_budgets");
        }
    }
}
