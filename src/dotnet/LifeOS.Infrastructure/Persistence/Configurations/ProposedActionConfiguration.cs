using LifeOS.Domain.ActionAgent;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.ActionAgent;
using LifeOS.Infrastructure.WeeklyReviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// AI-002: proposals of the Action Agent. User-owned (cascade with the user, ADR-006) and tied to the weekly
// review they were derived from (cascade with the review). The database backs the Domain state machine:
// allowlisted action type and status, the timestamps/failure code each status carries, and at most one
// open (Pending/Approved) proposal per review.
internal sealed class ProposedActionConfiguration : IEntityTypeConfiguration<ProposedActionRecord>
{
    public const string TableName = "proposed_actions";

    public const string UserForeignKeyName = "FK_proposed_actions_users_user_id";
    public const string ReviewForeignKeyName = "FK_proposed_actions_weekly_reviews_review_id";

    // The conflict target of every insert: one open proposal per review.
    public const string OpenPerReviewIndexName = "ux_proposed_actions_review_open";
    public const string OpenStatusesSql = "status IN ('Pending', 'Approved')";

    // A review's proposals, newest first.
    public const string ReviewIndexName = "ix_proposed_actions_review_created";

    public void Configure(EntityTypeBuilder<ProposedActionRecord> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_proposed_actions_action_type", "action_type IN ('MonthlyBudgetAdjustment')");
            table.HasCheckConstraint("ck_proposed_actions_status", "status IN ('Pending', 'Approved', 'Rejected', 'Executed', 'Failed')");
            table.HasCheckConstraint("ck_proposed_actions_state",
                """
                (status = 'Pending' AND decided_at_utc IS NULL AND executed_at_utc IS NULL AND failure_code IS NULL)
                OR (status IN ('Approved', 'Rejected') AND decided_at_utc IS NOT NULL AND executed_at_utc IS NULL AND failure_code IS NULL)
                OR (status = 'Executed' AND decided_at_utc IS NOT NULL AND executed_at_utc IS NOT NULL AND failure_code IS NULL)
                OR (status = 'Failed' AND decided_at_utc IS NOT NULL AND executed_at_utc IS NULL AND failure_code IS NOT NULL)
                """);
            table.HasCheckConstraint("ck_proposed_actions_payload", "payload_version >= 1 AND jsonb_typeof(payload) = 'object'");
            table.HasCheckConstraint("ck_proposed_actions_rationale", "length(btrim(rationale)) > 0");
            table.HasCheckConstraint("ck_proposed_actions_run",
                $"step_count BETWEEN 1 AND {ProposedAction.MaxSteps} AND cardinality(tool_calls) <= {ProposedAction.MaxToolCalls}");
        });

        builder.HasKey(proposal => proposal.Id);

        builder.Property(proposal => proposal.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(proposal => proposal.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(proposal => proposal.ReviewId)
            .HasColumnName("review_id")
            .IsRequired();

        builder.Property(proposal => proposal.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(proposal => proposal.Status)
            .HasColumnName("status")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(proposal => proposal.PayloadVersion)
            .HasColumnName("payload_version")
            .IsRequired();

        builder.Property(proposal => proposal.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(proposal => proposal.Rationale)
            .HasColumnName("rationale")
            .HasMaxLength(ProposedAction.MaxRationaleLength)
            .IsRequired();

        builder.Property(proposal => proposal.Provider)
            .HasColumnName("provider")
            .HasMaxLength(ProposedAction.MaxIdentityLength)
            .IsRequired();

        builder.Property(proposal => proposal.Model)
            .HasColumnName("model")
            .HasMaxLength(ProposedAction.MaxIdentityLength)
            .IsRequired();

        builder.Property(proposal => proposal.PromptVersion)
            .HasColumnName("prompt_version")
            .HasMaxLength(ProposedAction.MaxIdentityLength)
            .IsRequired();

        builder.Property(proposal => proposal.ToolSchemaVersion)
            .HasColumnName("tool_schema_version")
            .HasMaxLength(ProposedAction.MaxIdentityLength)
            .IsRequired();

        builder.Property(proposal => proposal.ToolCalls)
            .HasColumnName("tool_calls")
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(proposal => proposal.StepCount)
            .HasColumnName("step_count")
            .IsRequired();

        builder.Property(proposal => proposal.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(proposal => proposal.DecidedAtUtc)
            .HasColumnName("decided_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(proposal => proposal.ExecutedAtUtc)
            .HasColumnName("executed_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(proposal => proposal.FailureCode)
            .HasColumnName("failure_code")
            .HasMaxLength(ProposedAction.MaxFailureCodeLength);

        builder.HasIndex(proposal => proposal.ReviewId)
            .IsUnique()
            .HasFilter(OpenStatusesSql)
            .HasDatabaseName(OpenPerReviewIndexName);

        builder.HasIndex(proposal => new { proposal.ReviewId, proposal.CreatedAtUtc })
            .HasDatabaseName(ReviewIndexName);

        builder.HasIndex(proposal => proposal.UserId)
            .HasDatabaseName("ix_proposed_actions_user");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(proposal => proposal.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WeeklyReviewRecord>()
            .WithMany()
            .HasForeignKey(proposal => proposal.ReviewId)
            .HasConstraintName(ReviewForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
