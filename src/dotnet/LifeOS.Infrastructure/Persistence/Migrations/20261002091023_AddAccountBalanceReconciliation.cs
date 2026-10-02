using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountBalanceReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_reconciliations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_balance = table.Column<decimal>(type: "numeric", nullable: false),
                    observed_balance = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    effective_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_reconciliations", x => x.id);
                    table.UniqueConstraint("AK_account_reconciliations_id_account_id_user_id", x => new { x.id, x.account_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_account_reconciliations_accounts_account_id_user_id",
                        columns: x => new { x.account_id, x.user_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_account_reconciliations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_balance_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    observed_balance = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    effective_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_balance_adjustments", x => x.id);
                    table.CheckConstraint("ck_account_balance_adjustments_nonzero", "amount <> 0");
                    table.ForeignKey(
                        name: "FK_account_balance_adjustments_accounts_account_id_user_id",
                        columns: x => new { x.account_id, x.user_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_account_balance_adjustments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_adjustments_reconciliation_receipt",
                        columns: x => new { x.id, x.account_id, x.user_id },
                        principalTable: "account_reconciliations",
                        principalColumns: new[] { "id", "account_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_balance_adjustments_account_id_user_id",
                table: "account_balance_adjustments",
                columns: new[] { "account_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_account_balance_adjustments_id_account_id_user_id",
                table: "account_balance_adjustments",
                columns: new[] { "id", "account_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_account_balance_adjustments_user_account_time",
                table: "account_balance_adjustments",
                columns: new[] { "user_id", "account_id", "effective_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_account_reconciliations_user_id",
                table: "account_reconciliations",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_account_reconciliations_account_user_request",
                table: "account_reconciliations",
                columns: new[] { "account_id", "user_id", "request_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_balance_adjustments");

            migrationBuilder.DropTable(
                name: "account_reconciliations");
        }
    }
}
