using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGymPrograms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exercises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exercises", x => x.id);
                    table.UniqueConstraint("AK_exercises_id_user_id", x => new { x.id, x.user_id });
                    table.ForeignKey(
                        name: "FK_exercises_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workout_programs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_programs", x => x.id);
                    table.UniqueConstraint("AK_workout_programs_id_user_id", x => new { x.id, x.user_id });
                    table.ForeignKey(
                        name: "FK_workout_programs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workout_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_templates", x => x.id);
                    table.UniqueConstraint("AK_workout_templates_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_workout_templates_position", "position >= 1");
                    table.ForeignKey(
                        name: "FK_workout_templates_workout_programs",
                        columns: x => new { x.workout_program_id, x.user_id },
                        principalTable: "workout_programs",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workout_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rest_seconds = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_blocks", x => x.id);
                    table.UniqueConstraint("AK_workout_blocks_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_workout_blocks_kind", "kind IN ('Single', 'Superset')");
                    table.CheckConstraint("ck_workout_blocks_position", "position >= 1");
                    table.CheckConstraint("ck_workout_blocks_rest_seconds", "rest_seconds IS NULL OR rest_seconds BETWEEN 1 AND 3600");
                    table.ForeignKey(
                        name: "FK_workout_blocks_workout_templates",
                        columns: x => new { x.workout_template_id, x.user_id },
                        principalTable: "workout_templates",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workout_block_exercises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_block_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_block_exercises", x => x.id);
                    table.CheckConstraint("ck_workout_block_exercises_position", "position BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_workout_block_exercises_exercises",
                        columns: x => new { x.exercise_id, x.user_id },
                        principalTable: "exercises",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workout_block_exercises_workout_blocks",
                        columns: x => new { x.workout_block_id, x.user_id },
                        principalTable: "workout_blocks",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workout_set_prescriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_block_exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    target_min_reps = table.Column<int>(type: "integer", nullable: false),
                    target_max_reps = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workout_set_prescriptions", x => x.id);
                    table.CheckConstraint("ck_workout_set_prescriptions_position", "position >= 1");
                    table.CheckConstraint("ck_workout_set_prescriptions_reps", "target_min_reps >= 1 AND target_max_reps >= target_min_reps AND target_max_reps <= 999");
                    table.ForeignKey(
                        name: "FK_workout_set_prescriptions_workout_block_exercises",
                        column: x => x.workout_block_exercise_id,
                        principalTable: "workout_block_exercises",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_exercises_user_id",
                table: "exercises",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_workout_block_exercises_exercise_id_user_id",
                table: "workout_block_exercises",
                columns: new[] { "exercise_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_workout_block_exercises_workout_block_id_user_id",
                table: "workout_block_exercises",
                columns: new[] { "workout_block_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_workout_blocks_workout_template_id_user_id",
                table: "workout_blocks",
                columns: new[] { "workout_template_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_workout_programs_user_id",
                table: "workout_programs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_workout_set_prescriptions_workout_block_exercise_id",
                table: "workout_set_prescriptions",
                column: "workout_block_exercise_id");

            migrationBuilder.CreateIndex(
                name: "IX_workout_templates_workout_program_id_user_id",
                table: "workout_templates",
                columns: new[] { "workout_program_id", "user_id" });

            // Hand-written, PostgreSQL-specific SQL, NOT part of the EF model or the model snapshot
            // (EF Core cannot model expression indexes or deferrable constraints). EF migrations are
            // the canonical way to create them; see ExerciseConfiguration.NameIndexName and
            // WorkoutProgramConfiguration.SiblingPositionConstraintNames.
            //
            // Exercise names are unique per user, ignoring case.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_exercises_user_name
                    ON exercises (user_id, lower(name));
                """);

            // Sibling positions are unique per parent. Checked at commit, so one save may swap or
            // renumber positions (reorder, delete) without transient violations.
            migrationBuilder.Sql(
                """
                ALTER TABLE workout_templates
                    ADD CONSTRAINT ux_workout_templates_program_position
                    UNIQUE (workout_program_id, position) DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE workout_blocks
                    ADD CONSTRAINT ux_workout_blocks_template_position
                    UNIQUE (workout_template_id, position) DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE workout_block_exercises
                    ADD CONSTRAINT ux_workout_block_exercises_block_position
                    UNIQUE (workout_block_id, position) DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE workout_set_prescriptions
                    ADD CONSTRAINT ux_workout_set_prescriptions_exercise_position
                    UNIQUE (workout_block_exercise_id, position) DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workout_set_prescriptions");

            migrationBuilder.DropTable(
                name: "workout_block_exercises");

            migrationBuilder.DropTable(
                name: "exercises");

            migrationBuilder.DropTable(
                name: "workout_blocks");

            migrationBuilder.DropTable(
                name: "workout_templates");

            migrationBuilder.DropTable(
                name: "workout_programs");
        }
    }
}
