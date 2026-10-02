using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// The WorkoutSession aggregate (workout execution): workout_sessions → workout_session_blocks →
// workout_session_exercises → workout_session_sets. See docs/adr/0008-workout-execution-snapshots.md.
//
// Snapshot: the session owns a copy of the prescription taken at start. Nothing references the
// authoring tables except the two provenance columns, workout_program_id and workout_template_id,
// which are ON DELETE SET NULL: deleting a program or workout clears them and never deletes or
// changes a session. Only discarding the session itself cascades through its snapshot.
//
// Ownership backstop: sessions, blocks and exercises carry user_id; parent references are composite
// (…_id, user_id) foreign keys to (id, user_id), and so is the reference to the exercise, which is
// RESTRICT so an exercise with execution history cannot be deleted. Users are RESTRICT.
//
// At most one InProgress session per user: partial unique index on user_id.
internal sealed class WorkoutSessionConfiguration :
    IEntityTypeConfiguration<WorkoutSession>,
    IEntityTypeConfiguration<WorkoutSessionBlock>,
    IEntityTypeConfiguration<WorkoutSessionExercise>,
    IEntityTypeConfiguration<WorkoutSessionSet>
{
    public const string InProgressIndexName = "ux_workout_sessions_user_in_progress";
    public const string ProgramForeignKeyName = "FK_workout_sessions_workout_programs";
    public const string TemplateForeignKeyName = "FK_workout_sessions_workout_templates";
    public const string SessionForeignKeyName = "FK_workout_session_blocks_workout_sessions";
    public const string BlockForeignKeyName = "FK_workout_session_exercises_workout_session_blocks";
    public const string ExerciseForeignKeyName = "FK_workout_session_exercises_exercises";
    public const string SessionExerciseForeignKeyName = "FK_workout_session_sets_workout_session_exercises";

    public void Configure(EntityTypeBuilder<WorkoutSession> builder)
    {
        builder.ToTable("workout_sessions", table =>
        {
            table.HasCheckConstraint("ck_workout_sessions_status", "status IN ('InProgress', 'Completed')");
            table.HasCheckConstraint(
                "ck_workout_sessions_completion",
                "(status = 'InProgress' AND completed_at_utc IS NULL) "
                + "OR (status = 'Completed' AND completed_at_utc IS NOT NULL AND completed_at_utc >= started_at_utc)");
        });

        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(session => session.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasAlternateKey(session => new { session.Id, session.UserId });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(session => session.WorkoutProgramId)
            .HasColumnName("workout_program_id");

        builder.HasOne<WorkoutProgram>()
            .WithMany()
            .HasForeignKey(session => session.WorkoutProgramId)
            .HasConstraintName(ProgramForeignKeyName)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(session => session.WorkoutTemplateId)
            .HasColumnName("workout_template_id");

        builder.HasOne<WorkoutTemplate>()
            .WithMany()
            .HasForeignKey(session => session.WorkoutTemplateId)
            .HasConstraintName(TemplateForeignKeyName)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(session => session.ProgramName)
            .HasColumnName("program_name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(session => session.WorkoutName)
            .HasColumnName("workout_name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(session => session.StartedAtUtc)
            .HasColumnName("started_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(session => session.CompletedAtUtc)
            .HasColumnName("completed_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(session => session.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.HasIndex(session => session.UserId)
            .HasDatabaseName(InProgressIndexName)
            .IsUnique()
            .HasFilter("status = 'InProgress'");

        // Covers the user foreign key and per-user reads (the in-progress index is partial).
        builder.HasIndex(session => new { session.UserId, session.StartedAtUtc })
            .HasDatabaseName("ix_workout_sessions_user_started_at");

        builder.HasMany(session => session.Blocks)
            .WithOne()
            .HasForeignKey(block => new { block.WorkoutSessionId, block.UserId })
            .HasPrincipalKey(session => new { session.Id, session.UserId })
            .HasConstraintName(SessionForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(session => session.Blocks)
            .HasField("_blocks")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(session => session.ExecutionOrder);
        builder.Ignore(session => session.PrescribedSetCount);
        builder.Ignore(session => session.CompletedSetCount);
        builder.Ignore(session => session.Duration);
    }

    public void Configure(EntityTypeBuilder<WorkoutSessionBlock> builder)
    {
        builder.ToTable("workout_session_blocks", table =>
        {
            table.HasCheckConstraint("ck_workout_session_blocks_position", "position >= 1");
            table.HasCheckConstraint("ck_workout_session_blocks_kind", "kind IN ('Single', 'Superset')");
            table.HasCheckConstraint(
                "ck_workout_session_blocks_rest_seconds",
                $"rest_seconds IS NULL OR rest_seconds BETWEEN 1 AND {WorkoutBlock.MaxRestSeconds}");
        });

        builder.HasKey(block => block.Id);

        builder.Property(block => block.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(block => block.WorkoutSessionId)
            .HasColumnName("workout_session_id")
            .IsRequired();

        builder.Property(block => block.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasAlternateKey(block => new { block.Id, block.UserId });

        builder.Property(block => block.Position)
            .HasColumnName("position")
            .IsRequired();

        // A snapshot is never reordered, so a plain unique index keeps the order deterministic.
        builder.HasIndex(block => new { block.WorkoutSessionId, block.Position })
            .HasDatabaseName("ux_workout_session_blocks_session_position")
            .IsUnique();

        builder.Property(block => block.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(block => block.RestSeconds)
            .HasColumnName("rest_seconds");

        builder.HasMany(block => block.Exercises)
            .WithOne()
            .HasForeignKey(exercise => new { exercise.WorkoutSessionBlockId, exercise.UserId })
            .HasPrincipalKey(block => new { block.Id, block.UserId })
            .HasConstraintName(BlockForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(block => block.Exercises)
            .HasField("_exercises")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(block => block.ExecutionOrder);
    }

    public void Configure(EntityTypeBuilder<WorkoutSessionExercise> builder)
    {
        builder.ToTable("workout_session_exercises", table =>
            table.HasCheckConstraint("ck_workout_session_exercises_position", "position BETWEEN 1 AND 2"));

        builder.HasKey(exercise => exercise.Id);

        builder.Property(exercise => exercise.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(exercise => exercise.WorkoutSessionBlockId)
            .HasColumnName("workout_session_block_id")
            .IsRequired();

        builder.Property(exercise => exercise.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(exercise => exercise.ExerciseId)
            .HasColumnName("exercise_id")
            .IsRequired();

        builder.Property(exercise => exercise.Position)
            .HasColumnName("position")
            .IsRequired();

        builder.HasIndex(exercise => new { exercise.WorkoutSessionBlockId, exercise.Position })
            .HasDatabaseName("ux_workout_session_exercises_block_position")
            .IsUnique();

        builder.Property(exercise => exercise.Notes)
            .HasColumnName("notes")
            .HasColumnType("text");

        // (exercise_id, user_id) → exercises(id, user_id): only the owner's exercises; never cascaded.
        builder.HasOne<Exercise>()
            .WithMany()
            .HasForeignKey(exercise => new { exercise.ExerciseId, exercise.UserId })
            .HasPrincipalKey(exercise => new { exercise.Id, exercise.UserId })
            .HasConstraintName(ExerciseForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(exercise => exercise.Sets)
            .WithOne()
            .HasForeignKey(set => set.WorkoutSessionExerciseId)
            .HasConstraintName(SessionExerciseForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(exercise => exercise.Sets)
            .HasField("_sets")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    public void Configure(EntityTypeBuilder<WorkoutSessionSet> builder)
    {
        builder.ToTable("workout_session_sets", table =>
        {
            table.HasCheckConstraint("ck_workout_session_sets_position", "position >= 1");
            table.HasCheckConstraint(
                "ck_workout_session_sets_target_reps",
                $"target_min_reps >= 1 AND target_max_reps >= target_min_reps AND target_max_reps <= {RepRange.MaxReps}");
            // Pending: nothing recorded. Completed: reps, optional positive weight, completion time.
            table.HasCheckConstraint(
                "ck_workout_session_sets_actual",
                "(completed_at_utc IS NULL AND actual_reps IS NULL AND weight_kg IS NULL) "
                + $"OR (completed_at_utc IS NOT NULL AND actual_reps BETWEEN 1 AND {WorkoutSessionSet.MaxActualReps} "
                + $"AND (weight_kg IS NULL OR (weight_kg > 0 AND weight_kg <= {WorkoutSessionSet.MaxWeightKg:0})))");
        });

        builder.HasKey(set => set.Id);

        builder.Property(set => set.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(set => set.WorkoutSessionExerciseId)
            .HasColumnName("workout_session_exercise_id")
            .IsRequired();

        builder.Property(set => set.Position)
            .HasColumnName("position")
            .IsRequired();

        builder.HasIndex(set => new { set.WorkoutSessionExerciseId, set.Position })
            .HasDatabaseName("ux_workout_session_sets_exercise_position")
            .IsUnique();

        builder.Property(set => set.TargetMinReps)
            .HasColumnName("target_min_reps")
            .IsRequired();

        builder.Property(set => set.TargetMaxReps)
            .HasColumnName("target_max_reps")
            .IsRequired();

        builder.Property(set => set.ActualReps)
            .HasColumnName("actual_reps");

        // numeric(6,2) matches WorkoutSessionSet.MaxWeightKg and WeightDecimals.
        builder.Property(set => set.WeightKg)
            .HasColumnName("weight_kg")
            .HasPrecision(6, WorkoutSessionSet.WeightDecimals);

        builder.Property(set => set.CompletedAtUtc)
            .HasColumnName("completed_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(set => set.IsCompleted);
    }
}
