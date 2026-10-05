using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automation_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    automation_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurrence_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scheduled_for_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_executions", x => x.id);
                    table.CheckConstraint("ck_automation_executions_attempt_count", "attempt_count >= 1");
                    table.CheckConstraint("ck_automation_executions_state", "(status = 'Running' AND lease_expires_at_utc IS NOT NULL AND next_attempt_at_utc IS NULL AND completed_at_utc IS NULL) OR (status = 'FailedRetryable' AND lease_expires_at_utc IS NULL AND next_attempt_at_utc IS NOT NULL AND completed_at_utc IS NULL AND last_failure_code IS NOT NULL) OR (status = 'Succeeded' AND lease_expires_at_utc IS NULL AND next_attempt_at_utc IS NULL AND completed_at_utc IS NOT NULL AND last_failure_code IS NULL) OR (status = 'FailedFinal' AND lease_expires_at_utc IS NULL AND next_attempt_at_utc IS NULL AND completed_at_utc IS NOT NULL AND last_failure_code IS NOT NULL)");
                    table.CheckConstraint("ck_automation_executions_status", "status IN ('Running', 'Succeeded', 'FailedRetryable', 'FailedFinal')");
                    table.CheckConstraint("ck_automation_executions_window", "expires_at_utc > scheduled_for_utc");
                    table.ForeignKey(
                        name: "FK_automation_executions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_executions_retry",
                table: "automation_executions",
                column: "next_attempt_at_utc",
                filter: "status = 'FailedRetryable'");

            migrationBuilder.CreateIndex(
                name: "ix_automation_executions_stale",
                table: "automation_executions",
                column: "lease_expires_at_utc",
                filter: "status = 'Running'");

            migrationBuilder.CreateIndex(
                name: "ux_automation_executions_occurrence",
                table: "automation_executions",
                columns: new[] { "user_id", "automation_type", "occurrence_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_executions");
        }
    }
}
