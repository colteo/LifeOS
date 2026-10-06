using LifeOS.Domain.Notifications;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// AUTO-003A: at most one row per user; no row = the defaults (reminders on, quiet hours 22:00–08:00).
internal sealed class NotificationPreferencesConfiguration : IEntityTypeConfiguration<NotificationPreferences>
{
    public const string TableName = "notification_preferences";

    public const string UserForeignKeyName = "FK_notification_preferences_users_user_id";

    public void Configure(EntityTypeBuilder<NotificationPreferences> builder)
    {
        builder.ToTable(TableName, table =>
        {
            // QuietHours: two different local times, whole minutes.
            table.HasCheckConstraint("ck_notification_preferences_quiet_hours",
                "quiet_hours_start <> quiet_hours_end " +
                "AND EXTRACT(SECOND FROM quiet_hours_start) = 0 AND EXTRACT(SECOND FROM quiet_hours_end) = 0");
        });

        builder.HasKey(preferences => preferences.UserId);

        builder.Property(preferences => preferences.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();

        builder.Property(preferences => preferences.RecurringTransactionRemindersEnabled)
            .HasColumnName("recurring_transaction_reminders_enabled")
            .IsRequired();

        builder.Property(preferences => preferences.PlannedExpenseRemindersEnabled)
            .HasColumnName("planned_expense_reminders_enabled")
            .IsRequired();

        // Local wall-clock times in the user's zone (never UTC).
        builder.Property(preferences => preferences.QuietHoursStart)
            .HasColumnName("quiet_hours_start")
            .HasColumnType("time without time zone")
            .IsRequired();

        builder.Property(preferences => preferences.QuietHoursEnd)
            .HasColumnName("quiet_hours_end")
            .HasColumnType("time without time zone")
            .IsRequired();

        builder.Property(preferences => preferences.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Ignore(preferences => preferences.QuietHours);

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<NotificationPreferences>(preferences => preferences.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
