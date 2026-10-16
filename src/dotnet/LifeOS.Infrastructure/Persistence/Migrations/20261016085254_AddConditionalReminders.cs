using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConditionalReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // REM-001 R5: conditional reminders (HabitIncomplete, HydrationGoal), their per-type switches and the
            // NotRelevant delivery outcome (§6.6). Additive: existing rows are all Custom and stay valid.
            migrationBuilder.DropCheckConstraint(
                name: "ck_reminders_kind",
                table: "reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reminders_last_outcome",
                table: "reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reminders_title",
                table: "reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_deliveries_last_error_code",
                table: "notification_deliveries");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "reminders",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AddColumn<Guid>(
                name: "habit_id",
                table: "reminders",
                type: "uuid",
                nullable: true);

            // REM-001 D-12: on by default, like personal reminders. Saved rows get true; no row still means the
            // defaults. The repository always writes the columns explicitly.
            migrationBuilder.AddColumn<bool>(
                name: "habit_reminders_enabled",
                table: "notification_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "hydration_reminders_enabled",
                table: "notification_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "ix_reminders_habit_user",
                table: "reminders",
                columns: new[] { "habit_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_reminders_habit",
                table: "reminders",
                column: "habit_id",
                unique: true,
                filter: "kind = 'HabitIncomplete'");

            migrationBuilder.CreateIndex(
                name: "ux_reminders_user_hydration",
                table: "reminders",
                column: "user_id",
                unique: true,
                filter: "kind = 'HydrationGoal'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reminders_kind",
                table: "reminders",
                sql: "kind IN ('Custom', 'HabitIncomplete', 'HydrationGoal')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reminders_last_outcome",
                table: "reminders",
                sql: "last_outcome IS NULL OR last_outcome IN ('Notified', 'Missed', 'Disabled', 'Suppressed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reminders_shape",
                table: "reminders",
                sql: "((kind = 'Custom') = (title IS NOT NULL)) AND (kind = 'Custom' OR note IS NULL) AND ((kind = 'HabitIncomplete') = (habit_id IS NOT NULL)) AND (kind = 'Custom' OR schedule_kind <> 'Once')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reminders_title",
                table: "reminders",
                sql: "title IS NULL OR (length(btrim(title)) > 0 AND char_length(title) <= 80)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_deliveries_last_error_code",
                table: "notification_deliveries",
                sql: "last_error_code IS NULL OR last_error_code IN ('Transient', 'TokenInvalid', 'DeviceInactive', 'MaxAttempts', 'Expired', 'Rejected', 'NotRelevant')");

            migrationBuilder.AddForeignKey(
                name: "FK_reminders_habits",
                table: "reminders",
                columns: new[] { "habit_id", "user_id" },
                principalTable: "habits",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous shape allows only Custom reminders and the previous error codes: rows that only the
            // new shape allows are removed (conditional reminders) or mapped to the closest previous code.
            migrationBuilder.Sql("DELETE FROM reminders WHERE kind <> 'Custom';");
            migrationBuilder.Sql("UPDATE notification_deliveries SET last_error_code = 'Expired' WHERE last_error_code = 'NotRelevant';");
            migrationBuilder.Sql("UPDATE reminders SET last_outcome = 'Notified' WHERE last_outcome = 'Suppressed';");

            migrationBuilder.DropForeignKey(
                name: "FK_reminders_habits",
                table: "reminders");

            migrationBuilder.DropIndex(
                name: "ix_reminders_habit_user",
                table: "reminders");

            migrationBuilder.DropIndex(
                name: "ux_reminders_habit",
                table: "reminders");

            migrationBuilder.DropIndex(
                name: "ux_reminders_user_hydration",
                table: "reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reminders_kind",
                table: "reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reminders_last_outcome",
                table: "reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reminders_shape",
                table: "reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reminders_title",
                table: "reminders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_deliveries_last_error_code",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "habit_id",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "habit_reminders_enabled",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "hydration_reminders_enabled",
                table: "notification_preferences");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "reminders",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_reminders_kind",
                table: "reminders",
                sql: "kind IN ('Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reminders_last_outcome",
                table: "reminders",
                sql: "last_outcome IS NULL OR last_outcome IN ('Notified', 'Missed', 'Disabled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reminders_title",
                table: "reminders",
                sql: "length(btrim(title)) > 0 AND char_length(title) <= 80");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_deliveries_last_error_code",
                table: "notification_deliveries",
                sql: "last_error_code IS NULL OR last_error_code IN ('Transient', 'TokenInvalid', 'DeviceInactive', 'MaxAttempts', 'Expired', 'Rejected')");
        }
    }
}
