using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProposedActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "proposed_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payload_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    rationale = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tool_schema_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tool_calls = table.Column<string[]>(type: "text[]", nullable: false),
                    step_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    executed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proposed_actions", x => x.id);
                    table.CheckConstraint("ck_proposed_actions_action_type", "action_type IN ('MonthlyBudgetAdjustment')");
                    table.CheckConstraint("ck_proposed_actions_payload", "payload_version >= 1 AND jsonb_typeof(payload) = 'object'");
                    table.CheckConstraint("ck_proposed_actions_rationale", "length(btrim(rationale)) > 0");
                    table.CheckConstraint("ck_proposed_actions_run", "step_count BETWEEN 1 AND 8 AND cardinality(tool_calls) <= 8");
                    table.CheckConstraint("ck_proposed_actions_state", "(status = 'Pending' AND decided_at_utc IS NULL AND executed_at_utc IS NULL AND failure_code IS NULL)\nOR (status IN ('Approved', 'Rejected') AND decided_at_utc IS NOT NULL AND executed_at_utc IS NULL AND failure_code IS NULL)\nOR (status = 'Executed' AND decided_at_utc IS NOT NULL AND executed_at_utc IS NOT NULL AND failure_code IS NULL)\nOR (status = 'Failed' AND decided_at_utc IS NOT NULL AND executed_at_utc IS NULL AND failure_code IS NOT NULL)");
                    table.CheckConstraint("ck_proposed_actions_status", "status IN ('Pending', 'Approved', 'Rejected', 'Executed', 'Failed')");
                    table.ForeignKey(
                        name: "FK_proposed_actions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_proposed_actions_weekly_reviews_review_id",
                        column: x => x.review_id,
                        principalTable: "weekly_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_proposed_actions_review_created",
                table: "proposed_actions",
                columns: new[] { "review_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_proposed_actions_user",
                table: "proposed_actions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_proposed_actions_review_open",
                table: "proposed_actions",
                column: "review_id",
                unique: true,
                filter: "status IN ('Pending', 'Approved')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proposed_actions");
        }
    }
}
