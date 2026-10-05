using LifeOS.Domain.Automation;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public const string TableName = "notification_deliveries";

    // The logical notification identity per device (PD-7): the conflict target of every enqueue.
    public const string DeviceIndexName = "ux_notification_deliveries_device";

    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_notification_deliveries_status", "status IN ('Pending', 'Sending', 'Sent', 'Failed')");
            table.HasCheckConstraint("ck_notification_deliveries_attempt_count", "attempt_count >= 0");
            table.HasCheckConstraint("ck_notification_deliveries_window", "expires_at_utc > created_at_utc");
            table.HasCheckConstraint("ck_notification_deliveries_last_error_code",
                "last_error_code IS NULL OR last_error_code IN ('Transient', 'TokenInvalid', 'DeviceInactive', 'MaxAttempts', 'Expired', 'Rejected')");

            // Each status carries exactly its own fields: a next attempt only while Pending, a lease
            // (and at least one attempt) only while Sending, a sent time only once Sent, a code on Failed.
            table.HasCheckConstraint("ck_notification_deliveries_state",
                "(status = 'Pending' AND next_attempt_at_utc IS NOT NULL AND lease_expires_at_utc IS NULL AND sent_at_utc IS NULL) " +
                "OR (status = 'Sending' AND next_attempt_at_utc IS NULL AND lease_expires_at_utc IS NOT NULL AND sent_at_utc IS NULL AND attempt_count >= 1) " +
                "OR (status = 'Sent' AND next_attempt_at_utc IS NULL AND lease_expires_at_utc IS NULL AND sent_at_utc IS NOT NULL AND last_error_code IS NULL) " +
                "OR (status = 'Failed' AND next_attempt_at_utc IS NULL AND lease_expires_at_utc IS NULL AND sent_at_utc IS NULL AND last_error_code IS NOT NULL)");
        });

        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(delivery => delivery.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(delivery => delivery.DeviceRegistrationId)
            .HasColumnName("device_registration_id")
            .IsRequired();

        builder.Property(delivery => delivery.NotificationKey)
            .HasColumnName("notification_key")
            .HasMaxLength(NotificationDelivery.MaxNotificationKeyLength)
            .IsRequired();

        builder.Property(delivery => delivery.NotificationType)
            .HasColumnName("notification_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(delivery => delivery.SourceExecutionId)
            .HasColumnName("source_execution_id");

        builder.Property(delivery => delivery.ResourceType)
            .HasColumnName("resource_type")
            .HasMaxLength(NotificationDelivery.MaxResourceTypeLength);

        builder.Property(delivery => delivery.ResourceId)
            .HasColumnName("resource_id");

        builder.Property(delivery => delivery.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(delivery => delivery.AttemptCount)
            .HasColumnName("attempt_count")
            .IsRequired();

        builder.Property(delivery => delivery.NextAttemptAtUtc)
            .HasColumnName("next_attempt_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(delivery => delivery.LeaseExpiresAtUtc)
            .HasColumnName("lease_expires_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(delivery => delivery.ExpiresAtUtc)
            .HasColumnName("expires_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(delivery => delivery.LastErrorCode)
            .HasColumnName("last_error_code")
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(delivery => delivery.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(delivery => delivery.SentAtUtc)
            .HasColumnName("sent_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(delivery => new { delivery.NotificationKey, delivery.DeviceRegistrationId })
            .IsUnique()
            .HasDatabaseName(DeviceIndexName);

        builder.HasIndex(delivery => delivery.NextAttemptAtUtc)
            .HasDatabaseName("ix_notification_deliveries_due")
            .HasFilter("status = 'Pending'");

        builder.HasIndex(delivery => delivery.LeaseExpiresAtUtc)
            .HasDatabaseName("ix_notification_deliveries_stale")
            .HasFilter("status = 'Sending'");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(delivery => delivery.UserId)
            .HasConstraintName("FK_notification_deliveries_users_user_id")
            .OnDelete(DeleteBehavior.Cascade);

        // The device must belong to the delivery's user (ADR-006 composite key).
        builder.HasOne<DeviceRegistration>()
            .WithMany()
            .HasForeignKey(delivery => new { delivery.DeviceRegistrationId, delivery.UserId })
            .HasPrincipalKey(registration => new { registration.Id, registration.UserId })
            .HasConstraintName("FK_notification_deliveries_device_registrations")
            .OnDelete(DeleteBehavior.Cascade);

        // History outlives its execution's retention cleanup.
        builder.HasOne<AutomationExecution>()
            .WithMany()
            .HasForeignKey(delivery => delivery.SourceExecutionId)
            .HasConstraintName("FK_notification_deliveries_source_execution")
            .OnDelete(DeleteBehavior.SetNull);
    }
}
