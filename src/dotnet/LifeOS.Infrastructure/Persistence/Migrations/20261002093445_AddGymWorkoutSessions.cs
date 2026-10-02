using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGymWorkoutSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workout_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_program_id = table.Column<Guid>(type: "uuid", nullable: true),
                    workout_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    program_name = table.Column<string>(type: "text", nullable: false),
                    workout_name = table.Column<string>(type: "text", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_sessions", x => x.id);
                    table.UniqueConstraint("AK_workout_sessions_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_workout_sessions_completion", "(status = 'InProgress' AND completed_at_utc IS NULL) OR (status = 'Completed' AND completed_at_utc IS NOT NULL AND completed_at_utc >= started_at_utc)");
                    table.CheckConstraint("ck_workout_sessions_status", "status IN ('InProgress', 'Completed')");
                    table.ForeignKey(
                        name: "FK_workout_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workout_sessions_workout_programs",
                        column: x => x.workout_program_id,
                        principalTable: "workout_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_workout_sessions_workout_templates",
                        column: x => x.workout_template_id,
                        principalTable: "workout_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "workout_session_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rest_seconds = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_session_blocks", x => x.id);
                    table.UniqueConstraint("AK_workout_session_blocks_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_workout_session_blocks_kind", "kind IN ('Single', 'Superset')");
                    table.CheckConstraint("ck_workout_session_blocks_position", "position >= 1");
                    table.CheckConstraint("ck_workout_session_blocks_rest_seconds", "rest_seconds IS NULL OR rest_seconds BETWEEN 1 AND 3600");
                    table.ForeignKey(
                        name: "FK_workout_session_blocks_workout_sessions",
                        columns: x => new { x.workout_session_id, x.user_id },
                        principalTable: "workout_sessions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workout_session_exercises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_session_block_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_session_exercises", x => x.id);
                    table.CheckConstraint("ck_workout_session_exercises_position", "position BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_workout_session_exercises_exercises",
                        columns: x => new { x.exercise_id, x.user_id },
                        principalTable: "exercises",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workout_session_exercises_workout_session_blocks",
                        columns: x => new { x.workout_session_block_id, x.user_id },
                        principalTable: "workout_session_blocks",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workout_session_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_session_exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    target_min_reps = table.Column<int>(type: "integer", nullable: false),
                    target_max_reps = table.Column<int>(type: "integer", nullable: false),
                    actual_reps = table.Column<int>(type: "integer", nullable: true),
                    weight_kg = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_session_sets", x => x.id);
                    table.CheckConstraint("ck_workout_session_sets_actual", "(completed_at_utc IS NULL AND actual_reps IS NULL AND weight_kg IS NULL) OR (completed_at_utc IS NOT NULL AND actual_reps BETWEEN 1 AND 999 AND (weight_kg IS NULL OR (weight_kg > 0 AND weight_kg <= 1000)))");
                    table.CheckConstraint("ck_workout_session_sets_position", "position >= 1");
                    table.CheckConstraint("ck_workout_session_sets_target_reps", "target_min_reps >= 1 AND target_max_reps >= target_min_reps AND target_max_reps <= 999");
                    table.ForeignKey(
                        name: "FK_workout_session_sets_workout_session_exercises",
                        column: x => x.workout_session_exercise_id,
                        principalTable: "workout_session_exercises",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workout_session_blocks_workout_session_id_user_id",
                table: "workout_session_blocks",
                columns: new[] { "workout_session_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_workout_session_blocks_session_position",
                table: "workout_session_blocks",
                columns: new[] { "workout_session_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workout_session_exercises_exercise_id_user_id",
                table: "workout_session_exercises",
                columns: new[] { "exercise_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_workout_session_exercises_workout_session_block_id_user_id",
                table: "workout_session_exercises",
                columns: new[] { "workout_session_block_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_workout_session_exercises_block_position",
                table: "workout_session_exercises",
                columns: new[] { "workout_session_block_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_workout_session_sets_exercise_position",
                table: "workout_session_sets",
                columns: new[] { "workout_session_exercise_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_user_started_at",
                table: "workout_sessions",
                columns: new[] { "user_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_workout_sessions_workout_program_id",
                table: "workout_sessions",
                column: "workout_program_id");

            migrationBuilder.CreateIndex(
                name: "IX_workout_sessions_workout_template_id",
                table: "workout_sessions",
                column: "workout_template_id");

            migrationBuilder.CreateIndex(
                name: "ux_workout_sessions_user_in_progress",
                table: "workout_sessions",
                column: "user_id",
                unique: true,
                filter: "status = 'InProgress'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workout_session_sets");

            migrationBuilder.DropTable(
                name: "workout_session_exercises");

            migrationBuilder.DropTable(
                name: "workout_session_blocks");

            migrationBuilder.DropTable(
                name: "workout_sessions");
        }
    }
}
