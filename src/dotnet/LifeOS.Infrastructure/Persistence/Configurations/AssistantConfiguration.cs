using LifeOS.Domain.Assistant;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// AI-007 F1 §9.1-9.2. Chat data is user-owned and cascades with the user (ADR-006, AI-007 §9.5).
//
// Ownership: a message references its conversation through the composite (id, user_id) key, and an
// assistant reply references the user message it answers through (conversation_id, id), so no row can
// point at another user's conversation or at a message of another conversation. Deleting a
// conversation deletes its messages.
//
// Concurrency: next_sequence (conversations) and turn_status/turn_attempts (messages) are optimistic
// concurrency tokens. A writer that read a stale sequence or a turn attempt that was taken over saves
// nothing: SaveChanges fails as a whole. The unique indexes are the backstop for the same rules.
internal sealed class AssistantConfiguration :
    IEntityTypeConfiguration<AssistantConversation>,
    IEntityTypeConfiguration<AssistantMessage>
{
    public const string ConversationsTableName = "assistant_conversations";
    public const string MessagesTableName = "assistant_messages";

    public const string ConversationOwnerKeyName = "ux_assistant_conversations_id_user";
    public const string ConversationUserForeignKeyName = "FK_assistant_conversations_users_user_id";
    public const string ClientConversationIndexName = "ux_assistant_conversations_user_client";
    public const string ConversationListIndexName = "ix_assistant_conversations_user_updated";

    public const string MessageConversationKeyName = "ux_assistant_messages_conversation_id";
    public const string MessageConversationForeignKeyName = "FK_assistant_messages_conversations";
    public const string MessageReplyForeignKeyName = "FK_assistant_messages_reply_to";
    public const string SequenceIndexName = "ux_assistant_messages_conversation_sequence";
    public const string ClientMessageIndexName = "ux_assistant_messages_conversation_client";
    public const string ReplyIndexName = "ux_assistant_messages_conversation_reply";
    public const string ProcessingTurnIndexName = "ux_assistant_messages_conversation_processing";

    public void Configure(EntityTypeBuilder<AssistantConversation> builder)
    {
        builder.ToTable(ConversationsTableName, table =>
        {
            table.HasCheckConstraint("ck_assistant_conversations_next_sequence", $"next_sequence >= {AssistantConversation.FirstSequence}");
            table.HasCheckConstraint("ck_assistant_conversations_updated_at", "updated_at_utc >= created_at_utc");
        });

        builder.HasKey(conversation => conversation.Id);

        builder.Property(conversation => conversation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(conversation => conversation.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasAlternateKey(conversation => new { conversation.Id, conversation.UserId })
            .HasName(ConversationOwnerKeyName);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(conversation => conversation.UserId)
            .HasConstraintName(ConversationUserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(conversation => conversation.ClientConversationId)
            .HasColumnName("client_conversation_id")
            .IsRequired();

        builder.Property(conversation => conversation.NextSequence)
            .HasColumnName("next_sequence")
            .IsConcurrencyToken();

        builder.Property(conversation => conversation.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(conversation => conversation.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        // Idempotent create (AI-007 §11); also serves the user_id foreign key.
        builder.HasIndex(conversation => new { conversation.UserId, conversation.ClientConversationId })
            .IsUnique()
            .HasDatabaseName(ClientConversationIndexName);

        // The user's conversations, most recently active first.
        builder.HasIndex(conversation => new { conversation.UserId, conversation.UpdatedAtUtc, conversation.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName(ConversationListIndexName);
    }

    public void Configure(EntityTypeBuilder<AssistantMessage> builder)
    {
        builder.ToTable(MessagesTableName, table =>
        {
            table.HasCheckConstraint("ck_assistant_messages_role", "role IN ('User', 'Assistant')");
            table.HasCheckConstraint("ck_assistant_messages_turn_status",
                "turn_status IS NULL OR turn_status IN ('Processing', 'Completed', 'Failed')");
            table.HasCheckConstraint("ck_assistant_messages_sequence", $"sequence >= {AssistantConversation.FirstSequence}");
            table.HasCheckConstraint("ck_assistant_messages_content",
                "length(btrim(content)) > 0 AND char_length(content) <= "
                + $"CASE WHEN role = 'User' THEN {AssistantMessage.MaxUserContentLength} ELSE {AssistantMessage.MaxAssistantContentLength} END");
            // A user message carries a client id and a turn; an assistant message answers one user message.
            table.HasCheckConstraint("ck_assistant_messages_shape",
                """
                (role = 'User' AND client_message_id IS NOT NULL AND reply_to_message_id IS NULL
                    AND turn_status IS NOT NULL AND turn_attempts >= 1)
                OR (role = 'Assistant' AND client_message_id IS NULL AND reply_to_message_id IS NOT NULL
                    AND turn_status IS NULL AND turn_attempts IS NULL AND turn_lease_until_utc IS NULL AND failure_code IS NULL)
                """);
            // Processing carries a lease, Failed a failure code; neither appears in another state.
            table.HasCheckConstraint("ck_assistant_messages_turn",
                "turn_status IS NULL OR ((turn_status = 'Processing') = (turn_lease_until_utc IS NOT NULL) "
                + "AND (turn_status = 'Failed') = (failure_code IS NOT NULL))");
            table.HasCheckConstraint("ck_assistant_messages_failure_code",
                $"failure_code IS NULL OR failure_code ~ '^[a-z0-9_]{{1,{AssistantMessage.MaxFailureCodeLength}}}$'");
        });

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(message => message.ConversationId)
            .HasColumnName("conversation_id")
            .IsRequired();

        builder.Property(message => message.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // The principal of the reply reference: (conversation_id, id).
        builder.HasAlternateKey(message => new { message.ConversationId, message.Id })
            .HasName(MessageConversationKeyName);

        builder.HasOne<AssistantConversation>()
            .WithMany()
            .HasForeignKey(message => new { message.ConversationId, message.UserId })
            .HasPrincipalKey(conversation => new { conversation.Id, conversation.UserId })
            .HasConstraintName(MessageConversationForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AssistantMessage>()
            .WithMany()
            .HasForeignKey(message => new { message.ConversationId, message.ReplyToMessageId })
            .HasPrincipalKey(message => new { message.ConversationId, message.Id })
            .HasConstraintName(MessageReplyForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(message => message.Sequence)
            .HasColumnName("sequence");

        builder.Property(message => message.Role)
            .HasColumnName("role")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(message => message.Content)
            .HasColumnName("content")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(message => message.ClientMessageId)
            .HasColumnName("client_message_id");

        builder.Property(message => message.ReplyToMessageId)
            .HasColumnName("reply_to_message_id");

        builder.Property(message => message.TurnStatus)
            .HasColumnName("turn_status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsConcurrencyToken();

        builder.Property(message => message.TurnAttempts)
            .HasColumnName("turn_attempts")
            .IsConcurrencyToken();

        builder.Property(message => message.TurnLeaseUntilUtc)
            .HasColumnName("turn_lease_until_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(message => message.FailureCode)
            .HasColumnName("failure_code")
            .HasMaxLength(AssistantMessage.MaxFailureCodeLength);

        builder.Property(message => message.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(message => message.CanRetryAutomatically);

        // The only ordering key of a conversation; also serves the conversation foreign key.
        builder.HasIndex(message => new { message.ConversationId, message.Sequence })
            .IsUnique()
            .HasDatabaseName(SequenceIndexName);

        // A replayed client message id never stores a second message (AI-007 §11). Assistant messages
        // have no client id; NULLs are distinct.
        builder.HasIndex(message => new { message.ConversationId, message.ClientMessageId })
            .IsUnique()
            .HasDatabaseName(ClientMessageIndexName);

        // At most one reply per user message; also serves the reply foreign key.
        builder.HasIndex(message => new { message.ConversationId, message.ReplyToMessageId })
            .IsUnique()
            .HasDatabaseName(ReplyIndexName);

        // One turn at a time per conversation (AI-007 NFR-4): an interrupted turn must be failed before
        // the next message is accepted.
        builder.HasIndex(message => message.ConversationId)
            .IsUnique()
            .HasFilter("turn_status = 'Processing'")
            .HasDatabaseName(ProcessingTurnIndexName);
    }
}
