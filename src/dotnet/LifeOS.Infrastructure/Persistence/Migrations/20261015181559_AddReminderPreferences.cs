using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // REM-001 D-12: personal reminders are on by default. Saved rows get true; no row still means
            // the defaults. The repository always writes the column explicitly.
            migrationBuilder.AddColumn<bool>(
                name: "user_reminders_enabled",
                table: "notification_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "user_reminders_enabled",
                table: "notification_preferences");
        }
    }
}
