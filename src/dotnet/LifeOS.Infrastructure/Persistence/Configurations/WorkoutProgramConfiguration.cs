using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// The WorkoutProgram aggregate: workout_programs → workout_templates → workout_blocks →
// workout_block_exercises → workout_set_prescriptions.
//
// Ownership backstop: programs, workouts, blocks and block exercises carry user_id, and every parent
// reference is a composite (…_id, user_id) foreign key to (id, user_id); so is the reference to the
// exercise. A block can therefore only reference an exercise of the program's owner.
//
// Delete semantics: deleting a program cascades through its own descendants only. The exercise
// reference is RESTRICT, so exercises are never deleted with a program (and an exercise in use
// cannot be deleted). Users are RESTRICT, like Finance data.
//
// Order: sibling positions are unique per parent through DEFERRABLE INITIALLY DEFERRED unique
// constraints, checked at commit so a reorder may swap positions within one save. EF Core cannot
// model deferrable constraints, so they are NOT part of the EF model: they are created by raw SQL
// in the AddGymPrograms migration (see SiblingPositionConstraintNames).
internal sealed class WorkoutProgramConfiguration :
    IEntityTypeConfiguration<WorkoutProgram>,
    IEntityTypeConfiguration<WorkoutTemplate>,
    IEntityTypeConfiguration<WorkoutBlock>,
    IEntityTypeConfiguration<WorkoutBlockExercise>,
    IEntityTypeConfiguration<WorkoutSetPrescription>
{
    public const string WorkoutProgramForeignKeyName = "FK_workout_templates_workout_programs";
    public const string WorkoutTemplateForeignKeyName = "FK_workout_blocks_workout_templates";
    public const string WorkoutBlockForeignKeyName = "FK_workout_block_exercises_workout_blocks";
    public const string ExerciseForeignKeyName = "FK_workout_block_exercises_exercises";
    public const string WorkoutBlockExerciseForeignKeyName = "FK_workout_set_prescriptions_workout_block_exercises";

    public static readonly IReadOnlyCollection<string> SiblingPositionConstraintNames =
    [
        "ux_workout_templates_program_position",
        "ux_workout_blocks_template_position",
        "ux_workout_block_exercises_block_position",
        "ux_workout_set_prescriptions_exercise_position"
    ];

    public void Configure(EntityTypeBuilder<WorkoutProgram> builder)
    {
        builder.ToTable("workout_programs");

        builder.HasKey(program => program.Id);

        builder.Property(program => program.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(program => program.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasAlternateKey(program => new { program.Id, program.UserId });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(program => program.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(program => program.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(program => program.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasMany(program => program.Workouts)
            .WithOne()
            .HasForeignKey(workout => new { workout.WorkoutProgramId, workout.UserId })
            .HasPrincipalKey(program => new { program.Id, program.UserId })
            .HasConstraintName(WorkoutProgramForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(program => program.Workouts)
            .HasField("_workouts")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    public void Configure(EntityTypeBuilder<WorkoutTemplate> builder)
    {
        builder.ToTable("workout_templates", table =>
            table.HasCheckConstraint("ck_workout_templates_position", "position >= 1"));

        builder.HasKey(workout => workout.Id);

        builder.Property(workout => workout.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(workout => workout.WorkoutProgramId)
            .HasColumnName("workout_program_id")
            .IsRequired();

        builder.Property(workout => workout.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasAlternateKey(workout => new { workout.Id, workout.UserId });

        builder.Property(workout => workout.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(workout => workout.Position)
            .HasColumnName("position")
            .IsRequired();

        builder.HasMany(workout => workout.Blocks)
            .WithOne()
            .HasForeignKey(block => new { block.WorkoutTemplateId, block.UserId })
            .HasPrincipalKey(workout => new { workout.Id, workout.UserId })
            .HasConstraintName(WorkoutTemplateForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(workout => workout.Blocks)
            .HasField("_blocks")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    public void Configure(EntityTypeBuilder<WorkoutBlock> builder)
    {
        builder.ToTable("workout_blocks", table =>
        {
            table.HasCheckConstraint("ck_workout_blocks_position", "position >= 1");
            table.HasCheckConstraint("ck_workout_blocks_kind", "kind IN ('Single', 'Superset')");
            table.HasCheckConstraint(
                "ck_workout_blocks_rest_seconds",
                $"rest_seconds IS NULL OR rest_seconds BETWEEN 1 AND {WorkoutBlock.MaxRestSeconds}");
        });

        builder.HasKey(block => block.Id);

        builder.Property(block => block.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(block => block.WorkoutTemplateId)
            .HasColumnName("workout_template_id")
            .IsRequired();

        builder.Property(block => block.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasAlternateKey(block => new { block.Id, block.UserId });

        builder.Property(block => block.Position)
            .HasColumnName("position")
            .IsRequired();

        builder.Property(block => block.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(block => block.RestSeconds)
            .HasColumnName("rest_seconds");

        builder.HasMany(block => block.Exercises)
            .WithOne()
            .HasForeignKey(exercise => new { exercise.WorkoutBlockId, exercise.UserId })
            .HasPrincipalKey(block => new { block.Id, block.UserId })
            .HasConstraintName(WorkoutBlockForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(block => block.Exercises)
            .HasField("_exercises")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    public void Configure(EntityTypeBuilder<WorkoutBlockExercise> builder)
    {
        // A Single block uses position 1; a Superset 1 (A) and 2 (B). The exact count per kind is a
        // Domain rule.
        builder.ToTable("workout_block_exercises", table =>
            table.HasCheckConstraint("ck_workout_block_exercises_position", "position BETWEEN 1 AND 2"));

        builder.HasKey(exercise => exercise.Id);

        builder.Property(exercise => exercise.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(exercise => exercise.WorkoutBlockId)
            .HasColumnName("workout_block_id")
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
            .HasForeignKey(set => set.WorkoutBlockExerciseId)
            .HasConstraintName(WorkoutBlockExerciseForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(exercise => exercise.Sets)
            .HasField("_sets")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    public void Configure(EntityTypeBuilder<WorkoutSetPrescription> builder)
    {
        builder.ToTable("workout_set_prescriptions", table =>
        {
            table.HasCheckConstraint("ck_workout_set_prescriptions_position", "position >= 1");
            table.HasCheckConstraint(
                "ck_workout_set_prescriptions_reps",
                $"target_min_reps >= 1 AND target_max_reps >= target_min_reps AND target_max_reps <= {RepRange.MaxReps}");
        });

        builder.HasKey(set => set.Id);

        builder.Property(set => set.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(set => set.WorkoutBlockExerciseId)
            .HasColumnName("workout_block_exercise_id")
            .IsRequired();

        builder.Property(set => set.Position)
            .HasColumnName("position")
            .IsRequired();

        builder.Property(set => set.TargetMinReps)
            .HasColumnName("target_min_reps")
            .IsRequired();

        builder.Property(set => set.TargetMaxReps)
            .HasColumnName("target_max_reps")
            .IsRequired();
    }
}
