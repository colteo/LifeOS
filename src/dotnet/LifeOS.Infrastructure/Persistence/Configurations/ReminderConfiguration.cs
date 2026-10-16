using LifeOS.Domain.Reminders;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// REM-001 §10 (R2). next_due_local is a local wall-clock value (timestamp without time zone), not an
// instant; every instant is timestamptz. The schedule is stored as its parts (kind, once_date, ISO
// weekday bitmask, local_time). R5 widens ck_reminders_kind and makes title optional for its
// conditional kinds.
internal sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public const string TableName = "reminders";

    public const string UserForeignKeyName = "FK_reminders_users_user_id";

    public const string DueIndexName = "ix_reminders_due";
    public const string SnoozeIndexName = "ix_reminders_snooze";
    public const string UserIndexName = "ix_reminders_user";

    public void Configure(EntityTypeBuilder<Reminder> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_reminders_kind", "kind IN ('Custom')");
            table.HasCheckConstraint("ck_reminders_title",
                $"length(btrim(title)) > 0 AND char_length(title) <= {Reminder.TitleMaxLength}");
            table.HasCheckConstraint("ck_reminders_note",
                $"note IS NULL OR (length(btrim(note)) > 0 AND char_length(note) <= {Reminder.NoteMaxLength})");
            table.HasCheckConstraint("ck_reminders_schedule",
                "schedule_kind IN ('Once', 'Daily', 'Weekly') "
                + "AND (schedule_kind = 'Once') = (once_date IS NOT NULL) "
                + "AND (schedule_kind = 'Weekly') = (weekdays IS NOT NULL) "
                + $"AND (weekdays IS NULL OR weekdays BETWEEN 1 AND {ReminderSchedule.AllWeekdaysMask}) "
                + "AND (once_date IS NULL OR (once_date >= DATE '0001-01-08' AND once_date <= DATE '9999-12-23')) "
                + "AND EXTRACT(SECOND FROM local_time) = 0");
            table.HasCheckConstraint("ck_reminders_status", "status IN ('Active', 'Paused', 'Completed')");
            table.HasCheckConstraint("ck_reminders_next_due",
                "(status <> 'Active' OR next_due_local IS NOT NULL) "
                + "AND (status <> 'Completed' OR next_due_local IS NULL) "
                + "AND (next_due_local IS NULL OR EXTRACT(SECOND FROM next_due_local) = 0)");
            table.HasCheckConstraint("ck_reminders_snooze", "snooze_until_utc IS NULL OR EXTRACT(SECOND FROM snooze_until_utc) = 0");
            table.HasCheckConstraint("ck_reminders_last_outcome",
                "last_outcome IS NULL OR last_outcome IN ('Notified', 'Missed', 'Disabled')");
        });

        builder.HasKey(reminder => reminder.Id);

        builder.Property(reminder => reminder.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(reminder => reminder.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // Module data convention: a user with reminders cannot be deleted silently.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(reminder => reminder.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(reminder => reminder.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(reminder => reminder.Title)
            .HasColumnName("title")
            .HasMaxLength(Reminder.TitleMaxLength)
            .IsRequired();

        builder.Property(reminder => reminder.Note)
            .HasColumnName("note")
            .HasMaxLength(Reminder.NoteMaxLength);

        builder.Property(reminder => reminder.ScheduleKind)
            .HasColumnName("schedule_kind")
            .HasConversion<string>()
            .HasMaxLength(8);

        builder.Property(reminder => reminder.OnceDate)
            .HasColumnName("once_date")
            .HasColumnType("date");

        builder.Property(reminder => reminder.WeekdayMask)
            .HasColumnName("weekdays")
            .HasConversion<short?>();

        builder.Property(reminder => reminder.LocalTime)
            .HasColumnName("local_time")
            .HasColumnType("time without time zone");

        builder.Property(reminder => reminder.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(reminder => reminder.NextDueLocal)
            .HasColumnName("next_due_local")
            .HasColumnType("timestamp without time zone");

        builder.Property(reminder => reminder.SnoozeUntilUtc)
            .HasColumnName("snooze_until_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(reminder => reminder.LastTriggeredAtUtc)
            .HasColumnName("last_triggered_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(reminder => reminder.LastOutcome)
            .HasColumnName("last_outcome")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(reminder => reminder.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(reminder => reminder.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(reminder => reminder.Schedule);

        // Discovery (§6.2): Active reminders by next local occurrence, and pending snoozes.
        builder.HasIndex(reminder => reminder.NextDueLocal)
            .HasFilter("status = 'Active'")
            .HasDatabaseName(DueIndexName);

        builder.HasIndex(reminder => reminder.SnoozeUntilUtc)
            .HasFilter("snooze_until_utc IS NOT NULL")
            .HasDatabaseName(SnoozeIndexName);

        builder.HasIndex(reminder => reminder.UserId)
            .HasDatabaseName(UserIndexName);
    }
}
