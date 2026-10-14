using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHabits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "habits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    completion_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_habits", x => x.id);
                    table.UniqueConstraint("ux_habits_id_user", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_habits_archived", "(status = 'Archived') = (archived_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_habits_completion_source", "completion_source IN ('Manual', 'Hydration')");
                    table.CheckConstraint("ck_habits_description", "description IS NULL OR (length(btrim(description)) > 0 AND char_length(description) <= 500)");
                    table.CheckConstraint("ck_habits_name", "length(btrim(name)) > 0 AND char_length(name) <= 80");
                    table.CheckConstraint("ck_habits_starts_on", "starts_on >= DATE '0001-01-02' AND starts_on <= DATE '9999-12-30'");
                    table.CheckConstraint("ck_habits_status", "status IN ('Active', 'Archived')");
                    table.ForeignKey(
                        name: "FK_habits_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "habit_check_ins",
                columns: table => new
                {
                    habit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_habit_check_ins", x => new { x.habit_id, x.date });
                    table.CheckConstraint("ck_habit_check_ins_date", "date >= DATE '0001-01-02' AND date <= DATE '9999-12-30'");
                    table.CheckConstraint("ck_habit_check_ins_note", "note IS NULL OR (length(btrim(note)) > 0 AND char_length(note) <= 280)");
                    table.CheckConstraint("ck_habit_check_ins_status", "status IN ('Done', 'Excused')");
                    table.ForeignKey(
                        name: "FK_habit_check_ins_habits",
                        columns: x => new { x.habit_id, x.user_id },
                        principalTable: "habits",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "habit_schedule_versions",
                columns: table => new
                {
                    habit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    weekdays = table.Column<short>(type: "smallint", nullable: true),
                    times_per_week = table.Column<short>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_habit_schedule_versions", x => new { x.habit_id, x.effective_from });
                    table.CheckConstraint("ck_habit_schedule_versions_effective_from", "effective_from >= DATE '0001-01-02' AND effective_from <= DATE '9999-12-30'");
                    table.CheckConstraint("ck_habit_schedule_versions_frequency", "frequency IN ('Daily', 'Weekdays', 'TimesPerWeek', 'Inactive')");
                    table.CheckConstraint("ck_habit_schedule_versions_shape", "((frequency = 'Weekdays') = (weekdays IS NOT NULL)) AND ((frequency = 'TimesPerWeek') = (times_per_week IS NOT NULL)) AND (weekdays IS NULL OR weekdays BETWEEN 1 AND 127) AND (times_per_week IS NULL OR times_per_week BETWEEN 1 AND 7)");
                    table.ForeignKey(
                        name: "FK_habit_schedule_versions_habits",
                        columns: x => new { x.habit_id, x.user_id },
                        principalTable: "habits",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_habit_check_ins_habit_user",
                table: "habit_check_ins",
                columns: new[] { "habit_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_habit_check_ins_user_date",
                table: "habit_check_ins",
                columns: new[] { "user_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_habit_schedule_versions_habit_user",
                table: "habit_schedule_versions",
                columns: new[] { "habit_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_habits_user_status",
                table: "habits",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_habits_user_active_hydration",
                table: "habits",
                column: "user_id",
                unique: true,
                filter: "completion_source = 'Hydration' AND status = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "habit_check_ins");

            migrationBuilder.DropTable(
                name: "habit_schedule_versions");

            migrationBuilder.DropTable(
                name: "habits");
        }
    }
}
