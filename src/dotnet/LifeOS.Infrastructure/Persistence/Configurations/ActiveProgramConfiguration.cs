using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Gym.Training;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// The ActiveProgram aggregate (GYM-004): active_programs → active_program_completions.
//
// The template reference (workout_program_id, user_id) → workout_programs(id, user_id) cascades:
// deleting the program ends its active program and progress. Executed workouts are unaffected (they
// are snapshots, ADR-008); a completion only references its session, RESTRICT, and Completed
// sessions are never deleted. The completed workout id is NOT a foreign key: a workout removed from
// the program simply stops being required.
//
// Ownership backstop: completions carry user_id; references are composite (…_id, user_id).
//
// At most one Active program per user: partial unique index on user_id. A workout counts at most
// once per cycle, and a session at most once.
internal sealed class ActiveProgramConfiguration :
    IEntityTypeConfiguration<ActiveProgram>,
    IEntityTypeConfiguration<ActiveProgramCompletion>
{
    public const string ActiveIndexName = "ux_active_programs_user_active";
    public const string ProgramForeignKeyName = "FK_active_programs_workout_programs";
    public const string ActiveProgramForeignKeyName = "FK_active_program_completions_active_programs";
    public const string SessionForeignKeyName = "FK_active_program_completions_workout_sessions";

    public void Configure(EntityTypeBuilder<ActiveProgram> builder)
    {
        builder.ToTable("active_programs", table =>
        {
            table.HasCheckConstraint("ck_active_programs_status", "status IN ('Active', 'Completed', 'Stopped')");
            table.HasCheckConstraint(
                "ck_active_programs_cycles",
                $"total_cycles BETWEEN 1 AND {ActiveProgram.MaxCycles} AND current_cycle BETWEEN 1 AND total_cycles");
            table.HasCheckConstraint(
                "ck_active_programs_ended",
                "(status = 'Active' AND ended_at_utc IS NULL) "
                + "OR (status = 'Stopped' AND ended_at_utc IS NOT NULL AND ended_at_utc >= activated_at_utc) "
                + "OR (status = 'Completed' AND ended_at_utc IS NOT NULL AND ended_at_utc >= activated_at_utc AND current_cycle = total_cycles)");
        });

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

        builder.Property(program => program.WorkoutProgramId)
            .HasColumnName("workout_program_id")
            .IsRequired();

        builder.HasOne<WorkoutProgram>()
            .WithMany()
            .HasForeignKey(program => new { program.WorkoutProgramId, program.UserId })
            .HasPrincipalKey(program => new { program.Id, program.UserId })
            .HasConstraintName(ProgramForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(program => program.TotalCycles)
            .HasColumnName("total_cycles")
            .IsRequired();

        builder.Property(program => program.CurrentCycle)
            .HasColumnName("current_cycle")
            .IsRequired();

        builder.Property(program => program.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(program => program.ActivatedAtUtc)
            .HasColumnName("activated_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(program => program.EndedAtUtc)
            .HasColumnName("ended_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(program => program.UserId)
            .HasDatabaseName(ActiveIndexName)
            .IsUnique()
            .HasFilter("status = 'Active'");

        // Covers the template foreign key (the active index is partial and per user).
        builder.HasIndex(program => new { program.WorkoutProgramId, program.UserId })
            .HasDatabaseName("ix_active_programs_workout_program");

        builder.HasMany(program => program.Completions)
            .WithOne()
            .HasForeignKey(completion => new { completion.ActiveProgramId, completion.UserId })
            .HasPrincipalKey(program => new { program.Id, program.UserId })
            .HasConstraintName(ActiveProgramForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(program => program.Completions)
            .HasField("_completions")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(program => program.CurrentCycleCompletions);
    }

    public void Configure(EntityTypeBuilder<ActiveProgramCompletion> builder)
    {
        builder.ToTable("active_program_completions", table =>
            table.HasCheckConstraint("ck_active_program_completions_cycle", "cycle >= 1"));

        builder.HasKey(completion => completion.Id);

        builder.Property(completion => completion.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(completion => completion.ActiveProgramId)
            .HasColumnName("active_program_id")
            .IsRequired();

        builder.Property(completion => completion.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(completion => completion.Cycle)
            .HasColumnName("cycle")
            .IsRequired();

        builder.Property(completion => completion.WorkoutTemplateId)
            .HasColumnName("workout_template_id")
            .IsRequired();

        builder.Property(completion => completion.WorkoutSessionId)
            .HasColumnName("workout_session_id")
            .IsRequired();

        builder.Property(completion => completion.CompletedAtUtc)
            .HasColumnName("completed_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(completion => new { completion.ActiveProgramId, completion.Cycle, completion.WorkoutTemplateId })
            .HasDatabaseName("ux_active_program_completions_cycle_workout")
            .IsUnique();

        builder.HasIndex(completion => completion.WorkoutSessionId)
            .HasDatabaseName("ux_active_program_completions_session")
            .IsUnique();

        builder.HasOne<WorkoutSession>()
            .WithMany()
            .HasForeignKey(completion => new { completion.WorkoutSessionId, completion.UserId })
            .HasPrincipalKey(session => new { session.Id, session.UserId })
            .HasConstraintName(SessionForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
