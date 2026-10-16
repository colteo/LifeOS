using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reminders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    schedule_kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    once_date = table.Column<DateOnly>(type: "date", nullable: true),
                    weekdays = table.Column<short>(type: "smallint", nullable: true),
                    local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    next_due_local = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    snooze_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_triggered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminders", x => x.id);
                    table.CheckConstraint("ck_reminders_kind", "kind IN ('Custom')");
                    table.CheckConstraint("ck_reminders_last_outcome", "last_outcome IS NULL OR last_outcome IN ('Notified', 'Missed', 'Disabled')");
                    table.CheckConstraint("ck_reminders_next_due", "(status <> 'Active' OR next_due_local IS NOT NULL) AND (status <> 'Completed' OR next_due_local IS NULL) AND (next_due_local IS NULL OR EXTRACT(SECOND FROM next_due_local) = 0)");
                    table.CheckConstraint("ck_reminders_note", "note IS NULL OR (length(btrim(note)) > 0 AND char_length(note) <= 500)");
                    table.CheckConstraint("ck_reminders_schedule", "schedule_kind IN ('Once', 'Daily', 'Weekly') AND (schedule_kind = 'Once') = (once_date IS NOT NULL) AND (schedule_kind = 'Weekly') = (weekdays IS NOT NULL) AND (weekdays IS NULL OR weekdays BETWEEN 1 AND 127) AND (once_date IS NULL OR (once_date >= DATE '0001-01-08' AND once_date <= DATE '9999-12-23')) AND EXTRACT(SECOND FROM local_time) = 0");
                    table.CheckConstraint("ck_reminders_snooze", "snooze_until_utc IS NULL OR EXTRACT(SECOND FROM snooze_until_utc) = 0");
                    table.CheckConstraint("ck_reminders_status", "status IN ('Active', 'Paused', 'Completed')");
                    table.CheckConstraint("ck_reminders_title", "length(btrim(title)) > 0 AND char_length(title) <= 80");
                    table.ForeignKey(
                        name: "FK_reminders_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reminders_due",
                table: "reminders",
                column: "next_due_local",
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_reminders_snooze",
                table: "reminders",
                column: "snooze_until_utc",
                filter: "snooze_until_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_reminders_user",
                table: "reminders",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminders");
        }
    }
}
