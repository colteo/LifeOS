using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGymActivePrograms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "active_programs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    total_cycles = table.Column<int>(type: "integer", nullable: false),
                    current_cycle = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    activated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_active_programs", x => x.id);
                    table.UniqueConstraint("AK_active_programs_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_active_programs_cycles", "total_cycles BETWEEN 1 AND 99 AND current_cycle BETWEEN 1 AND total_cycles");
                    table.CheckConstraint("ck_active_programs_ended", "(status = 'Active' AND ended_at_utc IS NULL) OR (status = 'Stopped' AND ended_at_utc IS NOT NULL AND ended_at_utc >= activated_at_utc) OR (status = 'Completed' AND ended_at_utc IS NOT NULL AND ended_at_utc >= activated_at_utc AND current_cycle = total_cycles)");
                    table.CheckConstraint("ck_active_programs_status", "status IN ('Active', 'Completed', 'Stopped')");
                    table.ForeignKey(
                        name: "FK_active_programs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_active_programs_workout_programs",
                        columns: x => new { x.workout_program_id, x.user_id },
                        principalTable: "workout_programs",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "active_program_completions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    active_program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle = table.Column<int>(type: "integer", nullable: false),
                    workout_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_active_program_completions", x => x.id);
                    table.CheckConstraint("ck_active_program_completions_cycle", "cycle >= 1");
                    table.ForeignKey(
                        name: "FK_active_program_completions_active_programs",
                        columns: x => new { x.active_program_id, x.user_id },
                        principalTable: "active_programs",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_active_program_completions_workout_sessions",
                        columns: x => new { x.workout_session_id, x.user_id },
                        principalTable: "workout_sessions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_active_program_completions_active_program_id_user_id",
                table: "active_program_completions",
                columns: new[] { "active_program_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_active_program_completions_workout_session_id_user_id",
                table: "active_program_completions",
                columns: new[] { "workout_session_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_active_program_completions_cycle_workout",
                table: "active_program_completions",
                columns: new[] { "active_program_id", "cycle", "workout_template_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_active_program_completions_session",
                table: "active_program_completions",
                column: "workout_session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_active_programs_workout_program",
                table: "active_programs",
                columns: new[] { "workout_program_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_active_programs_user_active",
                table: "active_programs",
                column: "user_id",
                unique: true,
                filter: "status = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "active_program_completions");

            migrationBuilder.DropTable(
                name: "active_programs");
        }
    }
}
