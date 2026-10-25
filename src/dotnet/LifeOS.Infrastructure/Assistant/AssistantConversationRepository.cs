using LifeOS.Application.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Assistant;

// Every query filters by user_id; a foreign id behaves exactly like a missing one.
//
// TrySaveAsync is one SaveChanges, so one database transaction: the conversation's next_sequence, the
// changed turns and the new messages are stored together or not at all. A lost race surfaces as a
// failed concurrency token (next_sequence, turn_status/turn_attempts; also a row deleted meanwhile), a
// violation of one of the assistant's unique indexes, or a violation of a message foreign key (the
// conversation or the answered message was deleted). Those are reported as false after clearing the
// change tracker, so the caller can only go on with freshly read entities. Any other error propagates.
internal sealed class AssistantConversationRepository(LifeOSDbContext dbContext) : IAssistantConversationRepository
{
    private static readonly string[] LostRaceUniqueIndexes =
    [
        AssistantConfiguration.SequenceIndexName,
        AssistantConfiguration.ClientMessageIndexName,
        AssistantConfiguration.ReplyIndexName,
        AssistantConfiguration.ProcessingTurnIndexName
    ];

    private static readonly string[] LostRaceForeignKeys =
    [
        AssistantConfiguration.MessageConversationForeignKeyName,
        AssistantConfiguration.MessageReplyForeignKeyName
    ];

    public async Task<bool> TryAddAsync(AssistantConversation conversation, CancellationToken cancellationToken)
    {
        dbContext.AssistantConversations.Add(conversation);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception, AssistantConfiguration.ClientConversationIndexName))
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }

    public Task<AssistantConversation?> GetAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.AssistantConversations
            .SingleOrDefaultAsync(conversation => conversation.UserId == userId && conversation.Id == conversationId, cancellationToken);

    public Task<AssistantConversation?> GetByClientIdAsync(Guid userId, Guid clientConversationId, CancellationToken cancellationToken) =>
        dbContext.AssistantConversations
            .AsNoTracking()
            .SingleOrDefaultAsync(conversation => conversation.UserId == userId && conversation.ClientConversationId == clientConversationId,
                cancellationToken);

    public async Task<IReadOnlyList<AssistantConversation>> ListAsync(Guid userId, AssistantConversationCursor? before, int limit,
        CancellationToken cancellationToken)
    {
        var query = dbContext.AssistantConversations.AsNoTracking().Where(conversation => conversation.UserId == userId);

        if (before is not null)
        {
            var updatedAt = before.UpdatedAtUtc.ToUniversalTime();
            var id = before.Id;
            query = query.Where(conversation => conversation.UpdatedAtUtc < updatedAt
                || (conversation.UpdatedAtUtc == updatedAt && conversation.Id.CompareTo(id) < 0));
        }

        return await query
            .OrderByDescending(conversation => conversation.UpdatedAtUtc)
            .ThenByDescending(conversation => conversation.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        await dbContext.AssistantConversations
            .Where(conversation => conversation.UserId == userId && conversation.Id == conversationId)
            .ExecuteDeleteAsync(cancellationToken) == 1;

    public async Task<IReadOnlyList<AssistantMessage>> GetMessagesAsync(Guid userId, Guid conversationId, int afterSequence, int limit,
        CancellationToken cancellationToken) =>
        await dbContext.AssistantMessages
            .AsNoTracking()
            .Where(message => message.UserId == userId && message.ConversationId == conversationId && message.Sequence > afterSequence)
            .OrderBy(message => message.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<AssistantMessage?> GetUserMessageAsync(Guid userId, Guid conversationId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.AssistantMessages
            .SingleOrDefaultAsync(message => message.UserId == userId && message.ConversationId == conversationId && message.Id == messageId
                && message.Role == AssistantMessageRole.User, cancellationToken);

    public Task<AssistantMessage?> FindUserMessageAsync(Guid userId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.AssistantMessages
            .SingleOrDefaultAsync(message => message.UserId == userId && message.Id == messageId && message.Role == AssistantMessageRole.User,
                cancellationToken);

    public Task<AssistantMessage?> GetUserMessageByClientIdAsync(Guid userId, Guid conversationId, Guid clientMessageId,
        CancellationToken cancellationToken) =>
        dbContext.AssistantMessages
            .SingleOrDefaultAsync(message => message.UserId == userId && message.ConversationId == conversationId
                && message.ClientMessageId == clientMessageId, cancellationToken);

    public Task<AssistantMessage?> GetProcessingTurnAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.AssistantMessages
            .SingleOrDefaultAsync(message => message.UserId == userId && message.ConversationId == conversationId
                && message.TurnStatus == AssistantTurnStatus.Processing, cancellationToken);

    public Task<AssistantMessage?> GetReplyAsync(Guid userId, Guid conversationId, Guid userMessageId, CancellationToken cancellationToken) =>
        dbContext.AssistantMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.UserId == userId && message.ConversationId == conversationId
                && message.ReplyToMessageId == userMessageId, cancellationToken);

    public Task<bool> HasLaterUserMessageAsync(Guid userId, Guid conversationId, int sequence, CancellationToken cancellationToken) =>
        dbContext.AssistantMessages
            .AnyAsync(message => message.UserId == userId && message.ConversationId == conversationId
                && message.Role == AssistantMessageRole.User && message.Sequence > sequence, cancellationToken);

    // AI-007 F3: a pessimistic lock on top of the optimistic tokens, so the turn processor's whole completion
    // (the capability's draft change and the reply) runs while nothing else can change the turn or append to
    // the conversation: a send updates next_sequence, a retry or a recovery updates the message, a delete
    // removes the conversation row — all of them wait on these row locks.
    public async Task<bool> TryLockTurnAsync(Guid userId, Guid conversationId, Guid messageId, int attempt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Outside a transaction the lock would end with the statement and protect nothing.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The turn lock is taken inside a unit of work only.");
        }

        var at = AssistantConversation.ToStoredPrecision(now);
        var user = nameof(AssistantMessageRole.User);
        var processing = nameof(AssistantTurnStatus.Processing);

        var locked = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT m.id AS "Value"
            FROM assistant_messages m
            JOIN assistant_conversations c ON c.id = m.conversation_id AND c.user_id = m.user_id
            WHERE m.id = {messageId} AND m.conversation_id = {conversationId} AND m.user_id = {userId}
              AND m.role = {user} AND m.turn_status = {processing} AND m.turn_attempts = {attempt}
              AND m.turn_lease_until_utc > {at} AND m.sequence = c.next_sequence - 1
            FOR UPDATE OF c, m
            """).ToListAsync(cancellationToken);

        return locked.Count == 1;
    }

    public async Task<bool> TrySaveAsync(IReadOnlyCollection<AssistantMessage> added, CancellationToken cancellationToken)
    {
        dbContext.AssistantMessages.AddRange(added);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception, LostRaceUniqueIndexes)
            || PostgresErrors.IsForeignKeyViolation(exception, LostRaceForeignKeys))
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }
}
