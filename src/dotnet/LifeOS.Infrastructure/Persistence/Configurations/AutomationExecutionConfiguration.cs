using LifeOS.Domain.Automation;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class AutomationExecutionConfiguration : IEntityTypeConfiguration<AutomationExecution>
{
    public const string TableName = "automation_executions";

    // The logical occurrence identity (AUTO-001 §8): the conflict target of every claim.
    public const string OccurrenceIndexName = "ux_automation_executions_occurrence";

    public const string UserForeignKeyName = "FK_automation_executions_users_user_id";

    public void Configure(EntityTypeBuilder<AutomationExecution> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_automation_executions_status",
                "status IN ('Running', 'Succeeded', 'FailedRetryable', 'FailedFinal')");
            table.HasCheckConstraint("ck_automation_executions_attempt_count", "attempt_count >= 1");
            table.HasCheckConstraint("ck_automation_executions_window", "expires_at_utc > scheduled_for_utc");

            // Each status carries exactly its own fields: a lease only while Running, a next attempt
            // only while FailedRetryable, a completion time and (for failures) a code once terminal.
            table.HasCheckConstraint("ck_automation_executions_state",
                "(status = 'Running' AND lease_expires_at_utc IS NOT NULL AND next_attempt_at_utc IS NULL AND completed_at_utc IS NULL) " +
                "OR (status = 'FailedRetryable' AND lease_expires_at_utc IS NULL AND next_attempt_at_utc IS NOT NULL AND completed_at_utc IS NULL AND last_failure_code IS NOT NULL) " +
                "OR (status = 'Succeeded' AND lease_expires_at_utc IS NULL AND next_attempt_at_utc IS NULL AND completed_at_utc IS NOT NULL AND last_failure_code IS NULL) " +
                "OR (status = 'FailedFinal' AND lease_expires_at_utc IS NULL AND next_attempt_at_utc IS NULL AND completed_at_utc IS NOT NULL AND last_failure_code IS NOT NULL)");
        });

        builder.HasKey(execution => execution.Id);

        builder.Property(execution => execution.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(execution => execution.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(execution => execution.AutomationType)
            .HasColumnName("automation_type")
            .HasMaxLength(AutomationExecution.MaxAutomationTypeLength)
            .IsRequired();

        builder.Property(execution => execution.OccurrenceKey)
            .HasColumnName("occurrence_key")
            .HasMaxLength(AutomationExecution.MaxOccurrenceKeyLength)
            .IsRequired();

        builder.Property(execution => execution.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasMaxLength(User.MaxTimeZoneIdLength)
            .IsRequired();

        builder.Property(execution => execution.ScheduledForUtc)
            .HasColumnName("scheduled_for_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(execution => execution.ExpiresAtUtc)
            .HasColumnName("expires_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(execution => execution.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(execution => execution.AttemptCount)
            .HasColumnName("attempt_count")
            .IsRequired();

        builder.Property(execution => execution.LeaseExpiresAtUtc)
            .HasColumnName("lease_expires_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(execution => execution.NextAttemptAtUtc)
            .HasColumnName("next_attempt_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(execution => execution.LastFailureCode)
            .HasColumnName("last_failure_code")
            .HasMaxLength(AutomationExecution.MaxFailureCodeLength);

        // Opaque id of the module's artifact: deliberately no foreign key.
        builder.Property(execution => execution.ResultId)
            .HasColumnName("result_id");

        builder.Property(execution => execution.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(execution => execution.StartedAtUtc)
            .HasColumnName("started_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(execution => execution.CompletedAtUtc)
            .HasColumnName("completed_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(execution => new { execution.UserId, execution.AutomationType, execution.OccurrenceKey })
            .IsUnique()
            .HasDatabaseName(OccurrenceIndexName);

        builder.HasIndex(execution => execution.NextAttemptAtUtc)
            .HasDatabaseName("ix_automation_executions_retry")
            .HasFilter("status = 'FailedRetryable'");

        builder.HasIndex(execution => execution.LeaseExpiresAtUtc)
            .HasDatabaseName("ix_automation_executions_stale")
            .HasFilter("status = 'Running'");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(execution => execution.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
