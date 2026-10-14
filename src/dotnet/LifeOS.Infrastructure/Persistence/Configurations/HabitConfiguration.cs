using LifeOS.Domain.Habits;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// HAB-001 §9. Schedule versions and check-ins reference their habit through the composite (id, user_id)
// key (ADR-006), so a row can never point at another user's habit; both cascade with the habit.
internal sealed class HabitConfiguration :
    IEntityTypeConfiguration<Habit>,
    IEntityTypeConfiguration<HabitScheduleVersion>,
    IEntityTypeConfiguration<HabitCheckIn>
{
    public const string TableName = "habits";
    public const string VersionsTableName = "habit_schedule_versions";
    public const string CheckInsTableName = "habit_check_ins";

    public const string OwnerKeyName = "ux_habits_id_user";
    public const string UserForeignKeyName = "FK_habits_users_user_id";
    public const string VersionHabitForeignKeyName = "FK_habit_schedule_versions_habits";
    public const string CheckInHabitForeignKeyName = "FK_habit_check_ins_habits";

    // At most one active Hydration habit per user (HAB-001 §5.1 rule 12).
    public const string ActiveHydrationIndexName = "ux_habits_user_active_hydration";

    public void Configure(EntityTypeBuilder<Habit> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_habits_name",
                $"length(btrim(name)) > 0 AND char_length(name) <= {Habit.NameMaxLength}");
            table.HasCheckConstraint("ck_habits_description",
                $"description IS NULL OR (length(btrim(description)) > 0 AND char_length(description) <= {Habit.DescriptionMaxLength})");
            table.HasCheckConstraint("ck_habits_completion_source", "completion_source IN ('Manual', 'Hydration')");
            table.HasCheckConstraint("ck_habits_status", "status IN ('Active', 'Archived')");
            table.HasCheckConstraint("ck_habits_archived", "(status = 'Archived') = (archived_at_utc IS NOT NULL)");
            // Npgsql represents the DateOnly endpoints as PostgreSQL infinities, not calendar dates.
            table.HasCheckConstraint("ck_habits_starts_on", "starts_on >= DATE '0001-01-02' AND starts_on <= DATE '9999-12-30'");
        });

        builder.HasKey(habit => habit.Id);

        builder.Property(habit => habit.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(habit => habit.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasAlternateKey(habit => new { habit.Id, habit.UserId })
            .HasName(OwnerKeyName);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(habit => habit.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(habit => habit.Name)
            .HasColumnName("name")
            .HasMaxLength(Habit.NameMaxLength)
            .IsRequired();

        builder.Property(habit => habit.Description)
            .HasColumnName("description")
            .HasMaxLength(Habit.DescriptionMaxLength);

        builder.Property(habit => habit.CompletionSource)
            .HasColumnName("completion_source")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(habit => habit.StartsOn)
            .HasColumnName("starts_on")
            .HasColumnType("date");

        builder.Property(habit => habit.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(habit => habit.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(habit => habit.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(habit => habit.ArchivedAtUtc)
            .HasColumnName("archived_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(habit => new { habit.UserId, habit.Status })
            .HasDatabaseName("ix_habits_user_status");

        builder.HasIndex(habit => habit.UserId)
            .IsUnique()
            .HasFilter("completion_source = 'Hydration' AND status = 'Active'")
            .HasDatabaseName(ActiveHydrationIndexName);

        builder.HasMany(habit => habit.Schedule)
            .WithOne()
            .HasForeignKey(version => new { version.HabitId, version.UserId })
            .HasPrincipalKey(habit => new { habit.Id, habit.UserId })
            .HasConstraintName(VersionHabitForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(habit => habit.Schedule)
            .HasField("_schedule")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(habit => habit.CurrentFrequency);
        builder.Ignore(habit => habit.LastActiveFrequency);
    }

    public void Configure(EntityTypeBuilder<HabitScheduleVersion> builder)
    {
        builder.ToTable(VersionsTableName, table =>
        {
            table.HasCheckConstraint("ck_habit_schedule_versions_frequency",
                "frequency IN ('Daily', 'Weekdays', 'TimesPerWeek', 'Inactive')");
            // Weekdays carries a non-empty ISO bitmask, TimesPerWeek 1..7; the other kinds carry neither.
            table.HasCheckConstraint("ck_habit_schedule_versions_shape",
                "((frequency = 'Weekdays') = (weekdays IS NOT NULL)) AND ((frequency = 'TimesPerWeek') = (times_per_week IS NOT NULL)) "
                + $"AND (weekdays IS NULL OR weekdays BETWEEN 1 AND {WeekdaySet.AllMask}) "
                + $"AND (times_per_week IS NULL OR times_per_week BETWEEN {HabitFrequency.MinTimesPerWeek} AND {HabitFrequency.MaxTimesPerWeek})");
            table.HasCheckConstraint("ck_habit_schedule_versions_effective_from",
                "effective_from >= DATE '0001-01-02' AND effective_from <= DATE '9999-12-30'");
        });

        builder.HasKey(version => new { version.HabitId, version.EffectiveFrom });

        builder.Property(version => version.HabitId)
            .HasColumnName("habit_id");

        builder.Property(version => version.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(version => version.EffectiveFrom)
            .HasColumnName("effective_from")
            .HasColumnType("date");

        builder.Property(version => version.Kind)
            .HasColumnName("frequency")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(version => version.WeekdaysMask)
            .HasColumnName("weekdays")
            .HasConversion<short?>();

        builder.Property(version => version.TimesPerWeek)
            .HasColumnName("times_per_week")
            .HasConversion<short?>();

        builder.HasIndex(version => new { version.HabitId, version.UserId })
            .HasDatabaseName("ix_habit_schedule_versions_habit_user");

        builder.Ignore(version => version.Frequency);
    }

    public void Configure(EntityTypeBuilder<HabitCheckIn> builder)
    {
        builder.ToTable(CheckInsTableName, table =>
        {
            table.HasCheckConstraint("ck_habit_check_ins_status", "status IN ('Done', 'Excused')");
            table.HasCheckConstraint("ck_habit_check_ins_note",
                $"note IS NULL OR (length(btrim(note)) > 0 AND char_length(note) <= {HabitCheckIn.NoteMaxLength})");
            table.HasCheckConstraint("ck_habit_check_ins_date", "date >= DATE '0001-01-02' AND date <= DATE '9999-12-30'");
        });

        // One check-in per habit and date: also the conflict target of the upsert.
        builder.HasKey(checkIn => new { checkIn.HabitId, checkIn.Date });

        builder.Property(checkIn => checkIn.HabitId)
            .HasColumnName("habit_id");

        builder.Property(checkIn => checkIn.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<Habit>()
            .WithMany()
            .HasForeignKey(checkIn => new { checkIn.HabitId, checkIn.UserId })
            .HasPrincipalKey(habit => new { habit.Id, habit.UserId })
            .HasConstraintName(CheckInHabitForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(checkIn => checkIn.Date)
            .HasColumnName("date")
            .HasColumnType("date");

        builder.Property(checkIn => checkIn.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(checkIn => checkIn.Note)
            .HasColumnName("note")
            .HasMaxLength(HabitCheckIn.NoteMaxLength);

        builder.Property(checkIn => checkIn.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(checkIn => checkIn.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        // Home / day queries.
        builder.HasIndex(checkIn => new { checkIn.UserId, checkIn.Date })
            .HasDatabaseName("ix_habit_check_ins_user_date");

        // Covers the composite habit foreign key.
        builder.HasIndex(checkIn => new { checkIn.HabitId, checkIn.UserId })
            .HasDatabaseName("ix_habit_check_ins_habit_user");
    }
}
