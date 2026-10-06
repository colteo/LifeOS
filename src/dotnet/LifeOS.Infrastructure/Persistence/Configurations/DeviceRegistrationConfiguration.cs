using LifeOS.Domain.Notifications;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class DeviceRegistrationConfiguration : IEntityTypeConfiguration<DeviceRegistration>
{
    public const string TableName = "device_registrations";

    // One row per (installation, owner): the conflict target of the registration upsert.
    public const string InstallationUserIndexName = "ux_device_registrations_installation_user";

    // At most one Active row per installation: one signed-in owner.
    public const string ActiveInstallationIndexName = "ux_device_registrations_installation_active";

    // A token is never held by two rows.
    public const string TokenIndexName = "ux_device_registrations_token";

    public const string UserForeignKeyName = "FK_device_registrations_users_user_id";

    public void Configure(EntityTypeBuilder<DeviceRegistration> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_device_registrations_platform", "platform IN ('Android')");
            table.HasCheckConstraint("ck_device_registrations_push_provider", "push_provider IN ('Fcm')");
            table.HasCheckConstraint("ck_device_registrations_status", "status IN ('Active', 'Inactive')");
            table.HasCheckConstraint("ck_device_registrations_inactive_reason",
                "inactive_reason IS NULL OR inactive_reason IN ('PermissionDenied', 'SignedOut', 'TokenInvalid')");

            // Active ⇔ token present; Inactive rows keep no token and say why.
            table.HasCheckConstraint("ck_device_registrations_state",
                "(status = 'Active' AND push_token IS NOT NULL AND inactive_reason IS NULL) " +
                "OR (status = 'Inactive' AND push_token IS NULL AND inactive_reason IS NOT NULL)");
        });

        builder.HasKey(registration => registration.Id);

        // Target of the composite delivery FK (ADR-006): a delivery's device belongs to its user.
        builder.HasAlternateKey(registration => new { registration.Id, registration.UserId })
            .HasName("ux_device_registrations_id_user");

        builder.Property(registration => registration.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(registration => registration.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(registration => registration.InstallationId)
            .HasColumnName("installation_id")
            .HasMaxLength(DeviceRegistration.MaxInstallationIdLength)
            .IsRequired();

        builder.Property(registration => registration.Platform)
            .HasColumnName("platform")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(registration => registration.PushProvider)
            .HasColumnName("push_provider")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // Sensitive: never logged or returned.
        builder.Property(registration => registration.PushToken)
            .HasColumnName("push_token")
            .HasMaxLength(DeviceRegistration.MaxPushTokenLength);

        builder.Property(registration => registration.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(registration => registration.InactiveReason)
            .HasColumnName("inactive_reason")
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(registration => registration.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(registration => registration.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(registration => registration.LastSeenAtUtc)
            .HasColumnName("last_seen_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(registration => new { registration.InstallationId, registration.UserId })
            .IsUnique()
            .HasDatabaseName(InstallationUserIndexName);

        builder.HasIndex(registration => registration.InstallationId)
            .IsUnique()
            .HasDatabaseName(ActiveInstallationIndexName)
            .HasFilter("status = 'Active'");

        builder.HasIndex(registration => new { registration.PushProvider, registration.PushToken })
            .IsUnique()
            .HasDatabaseName(TokenIndexName)
            .HasFilter("push_token IS NOT NULL");

        builder.HasIndex(registration => registration.UserId)
            .HasDatabaseName("ix_device_registrations_user_active")
            .HasFilter("status = 'Active'");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(registration => registration.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
