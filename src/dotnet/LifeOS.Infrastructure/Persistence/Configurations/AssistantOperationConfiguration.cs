using LifeOS.Domain.Assistant;
using LifeOS.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// AI-007 F2 §9.3: operations the assistant prepares in a conversation. The database backs the Domain state
// machine (AssistantOperation):
//
//   - ownership: the operation references its conversation through the composite (id, user_id) key, so it
//     can never belong to another user than its conversation; deleting the conversation (or the user, which
//     cascades to it) deletes its operations;
//   - at most one open (Collecting/AwaitingConfirmation) operation per conversation (partial unique index,
//     also the conflict target of the insert);
//   - allowlisted kind and status; Executed exactly when result_transaction_id and executed_at_utc are set;
//     versioned jsonb draft;
//   - result_transaction_id is unique: one transaction is the result of at most one operation.
//
// result_transaction_id deliberately has NO foreign key to transactions (AI-007 D-8): Finance owns its
// transactions. A foreign key would either block deleting a transaction in Finance (RESTRICT), rewrite the
// operation's history (SET NULL, which the Executed check forbids anyway) or delete Finance data with chat
// data (CASCADE in the other direction is not possible, and must not be). Deleting a conversation never
// touches transactions; the id is written only by the confirmation, in the same database transaction that
// created that transaction for the same user, so it always referred to a real transaction of the owner.
internal sealed class AssistantOperationConfiguration : IEntityTypeConfiguration<AssistantOperationRecord>
{
    public const string TableName = "assistant_operations";

    public const string ConversationForeignKeyName = "FK_assistant_operations_conversations";
    public const string OpenPerConversationIndexName = "ux_assistant_operations_conversation_open";
    public const string ResultTransactionIndexName = "ux_assistant_operations_result_transaction";
    public const string ConversationIndexName = "ix_assistant_operations_conversation";

    public const string OpenStatusesSql = "status IN ('Collecting', 'AwaitingConfirmation')";

    public void Configure(EntityTypeBuilder<AssistantOperationRecord> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_assistant_operations_kind",
                $"kind IN ({string.Join(", ", AssistantOperationKinds.All.Select(kind => $"'{kind}'"))})");
            table.HasCheckConstraint("ck_assistant_operations_status",
                $"status IN ({string.Join(", ", Enum.GetNames<AssistantOperationStatus>().Select(status => $"'{status}'"))})");
            table.HasCheckConstraint("ck_assistant_operations_result",
                "(status = 'Executed') = (result_transaction_id IS NOT NULL) AND (status = 'Executed') = (executed_at_utc IS NOT NULL)");
            table.HasCheckConstraint("ck_assistant_operations_draft",
                "draft_version >= 1 AND payload_version >= 1 AND jsonb_typeof(draft) = 'object'");
            table.HasCheckConstraint("ck_assistant_operations_times",
                "updated_at_utc >= created_at_utc AND expires_at_utc > created_at_utc");
        });

        builder.HasKey(operation => operation.Id);

        builder.Property(operation => operation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(operation => operation.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(operation => operation.ConversationId)
            .HasColumnName("conversation_id")
            .IsRequired();

        builder.Property(operation => operation.Kind)
            .HasColumnName("kind")
            .HasMaxLength(AssistantOperationKinds.MaxLength)
            .IsRequired();

        builder.Property(operation => operation.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(operation => operation.DraftVersion)
            .HasColumnName("draft_version")
            .IsRequired();

        builder.Property(operation => operation.PayloadVersion)
            .HasColumnName("payload_version")
            .IsRequired();

        builder.Property(operation => operation.Draft)
            .HasColumnName("draft")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(operation => operation.ExpiresAtUtc)
            .HasColumnName("expires_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(operation => operation.ResultTransactionId)
            .HasColumnName("result_transaction_id");

        builder.Property(operation => operation.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(operation => operation.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(operation => operation.ExecutedAtUtc)
            .HasColumnName("executed_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<AssistantConversation>()
            .WithMany()
            .HasForeignKey(operation => new { operation.ConversationId, operation.UserId })
            .HasPrincipalKey(conversation => new { conversation.Id, conversation.UserId })
            .HasConstraintName(ConversationForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        // One open operation per conversation (AI-007 P-7).
        builder.HasIndex(operation => operation.ConversationId)
            .IsUnique()
            .HasFilter(OpenStatusesSql)
            .HasDatabaseName(OpenPerConversationIndexName);

        // At most one operation per transaction (AI-007 P-8, NFR-2).
        builder.HasIndex(operation => operation.ResultTransactionId)
            .IsUnique()
            .HasFilter("result_transaction_id IS NOT NULL")
            .HasDatabaseName(ResultTransactionIndexName);

        // A conversation's operations in creation order; also serves the foreign key and its cascade.
        builder.HasIndex(operation => new { operation.ConversationId, operation.UserId, operation.CreatedAtUtc })
            .HasDatabaseName(ConversationIndexName);
    }
}
