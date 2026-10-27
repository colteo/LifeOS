using LifeOS.Application.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Assistant;

// The jsonb document of one operation kind: its kind name and the reader/writer of its versioned draft. The stored
// payload_version selects the reader; an unknown version is an error, never a guess.
internal interface IAssistantDraftCodec<TDraft> where TDraft : class, IAssistantDraft
{
    string Kind { get; }

    string Serialize(int payloadVersion, TDraft draft);

    TDraft Deserialize(int payloadVersion, string json);
}

// AI-007 F2 on PostgreSQL, for any operation kind (AI-008: shared by Finance, Hydration and Meals). Reads are
// owner-scoped, kind-scoped and untracked. The insert is one statement that the partial unique index (one open
// operation per conversation, whatever its kind) decides between concurrent requests, and the composite conversation
// foreign key rejects a conversation that is missing or another user's. Every change is one conditional UPDATE on
// (id, owner, kind, status, draft_version); the confirmation additionally locks the row (SELECT ... FOR UPDATE) for
// the rest of its transaction. No statement here uses SaveChanges, so nothing tracked by the request's DbContext is
// written as a side effect.
internal sealed class AssistantOperationRepository<TDraft>(LifeOSDbContext db, IAssistantDraftCodec<TDraft> codec)
    : IAssistantOperationRepository<TDraft> where TDraft : class, IAssistantDraft
{
    public async Task<AssistantOperationAddOutcome> TryAddAsync(AssistantOperation<TDraft> operation, CancellationToken cancellationToken)
    {
        if (!operation.IsOpen || operation.DraftVersion != AssistantOperation<TDraft>.FirstDraftVersion || operation.Kind != codec.Kind)
        {
            throw new ArgumentException("Only a new open operation of this kind can be added.", nameof(operation));
        }

        var row = ToRecord(operation);

        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO assistant_operations
                    (id, user_id, conversation_id, kind, status, draft_version, payload_version, draft, expires_at_utc,
                     result_id, created_at_utc, updated_at_utc, executed_at_utc)
                VALUES
                    ({row.Id}, {row.UserId}, {row.ConversationId}, {row.Kind}, {row.Status}, {row.DraftVersion}, {row.PayloadVersion},
                     CAST({row.Draft} AS jsonb), {row.ExpiresAtUtc}, NULL, {row.CreatedAtUtc}, {row.UpdatedAtUtc}, NULL)
                ON CONFLICT (conversation_id) WHERE status IN ('Collecting', 'AwaitingConfirmation') DO NOTHING
                """, cancellationToken);

            return inserted == 1 ? AssistantOperationAddOutcome.Added : AssistantOperationAddOutcome.OpenOperationExists;
        }
        catch (Exception exception) when (PostgresErrors.IsForeignKeyViolation(exception, AssistantOperationConfiguration.ConversationForeignKeyName))
        {
            return AssistantOperationAddOutcome.ConversationNotFound;
        }
    }

    public async Task<AssistantOperation<TDraft>?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken)
    {
        var row = await Operations()
            .SingleOrDefaultAsync(operation => operation.Id == operationId && operation.UserId == userId, cancellationToken);

        return row is null ? null : ToDomain(row);
    }

    public async Task<AssistantOperation<TDraft>?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var collecting = nameof(AssistantOperationStatus.Collecting);
        var awaiting = nameof(AssistantOperationStatus.AwaitingConfirmation);

        var row = await Operations()
            .SingleOrDefaultAsync(operation => operation.ConversationId == conversationId && operation.UserId == userId
                && (operation.Status == collecting || operation.Status == awaiting), cancellationToken);

        return row is null ? null : ToDomain(row);
    }

    public async Task<bool> TryUpdateAsync(AssistantOperation<TDraft> operation, AssistantOperationStatus expectedStatus, int expectedDraftVersion,
        CancellationToken cancellationToken)
    {
        var row = ToRecord(operation);
        var expected = expectedStatus.ToString();
        var kind = codec.Kind;

        return await db.Set<AssistantOperationRecord>()
            .Where(stored => stored.Id == row.Id && stored.UserId == row.UserId && stored.Kind == kind
                && stored.Status == expected && stored.DraftVersion == expectedDraftVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(stored => stored.Status, row.Status)
                .SetProperty(stored => stored.DraftVersion, row.DraftVersion)
                .SetProperty(stored => stored.PayloadVersion, row.PayloadVersion)
                .SetProperty(stored => stored.Draft, row.Draft)
                .SetProperty(stored => stored.ExpiresAtUtc, row.ExpiresAtUtc)
                .SetProperty(stored => stored.ResultId, row.ResultId)
                .SetProperty(stored => stored.UpdatedAtUtc, row.UpdatedAtUtc)
                .SetProperty(stored => stored.ExecutedAtUtc, row.ExecutedAtUtc), cancellationToken) == 1;
    }

    public async Task<bool> TryLockForConfirmationAsync(Guid userId, Guid operationId, int expectedDraftVersion, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Outside a transaction the lock would end with the statement and protect nothing.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The confirmation lock is taken inside a unit of work only.");
        }

        var at = AssistantConversation.ToStoredPrecision(now);
        var kind = codec.Kind;

        var locked = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM assistant_operations
            WHERE id = {operationId} AND user_id = {userId} AND kind = {kind}
              AND status = 'AwaitingConfirmation' AND draft_version = {expectedDraftVersion} AND expires_at_utc > {at}
            FOR UPDATE
            """).ToListAsync(cancellationToken);

        return locked.Count == 1;
    }

    private IQueryable<AssistantOperationRecord> Operations()
    {
        var kind = codec.Kind;

        return db.Set<AssistantOperationRecord>().AsNoTracking().Where(operation => operation.Kind == kind);
    }

    private AssistantOperationRecord ToRecord(AssistantOperation<TDraft> operation) => new()
    {
        Id = operation.Id,
        UserId = operation.UserId,
        ConversationId = operation.ConversationId,
        Kind = operation.Kind,
        Status = operation.Status.ToString(),
        DraftVersion = operation.DraftVersion,
        PayloadVersion = operation.PayloadVersion,
        Draft = codec.Serialize(operation.PayloadVersion, operation.Draft),
        ExpiresAtUtc = operation.ExpiresAtUtc,
        ResultId = operation.ResultId,
        CreatedAtUtc = operation.CreatedAtUtc,
        UpdatedAtUtc = operation.UpdatedAtUtc,
        ExecutedAtUtc = operation.ExecutedAtUtc
    };

    private AssistantOperation<TDraft> ToDomain(AssistantOperationRecord row) => AssistantOperation<TDraft>.Restore(
        row.Id,
        row.UserId,
        row.ConversationId,
        row.Kind,
        Enum.Parse<AssistantOperationStatus>(row.Status),
        row.DraftVersion,
        row.PayloadVersion,
        codec.Deserialize(row.PayloadVersion, row.Draft),
        row.ExpiresAtUtc,
        row.ResultId,
        row.CreatedAtUtc,
        row.UpdatedAtUtc,
        row.ExecutedAtUtc);
}

// AI-008: the kind-agnostic reads of IAssistantOperationReader. The header never reads a draft document, so a
// request can be routed to its module before anything kind-specific is parsed.
internal sealed class AssistantOperationReader(LifeOSDbContext db) : IAssistantOperationReader
{
    public async Task<AssistantOperationHeader?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken)
    {
        var row = await Headers(operation => operation.Id == operationId && operation.UserId == userId).SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : ToHeader(row);
    }

    public async Task<AssistantOperationHeader?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var collecting = nameof(AssistantOperationStatus.Collecting);
        var awaiting = nameof(AssistantOperationStatus.AwaitingConfirmation);

        var row = await Headers(operation => operation.ConversationId == conversationId && operation.UserId == userId
                && (operation.Status == collecting || operation.Status == awaiting))
            .SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : ToHeader(row);
    }

    public async Task<bool> TryExpireAsync(Guid userId, Guid operationId, AssistantOperationStatus expectedStatus, int expectedDraftVersion,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!AssistantOperation<IAssistantDraft>.IsOpenStatus(expectedStatus))
        {
            return false;
        }

        var expected = expectedStatus.ToString();
        var expired = nameof(AssistantOperationStatus.Expired);
        var at = AssistantConversation.ToStoredPrecision(now);

        return await db.Set<AssistantOperationRecord>()
            .Where(stored => stored.Id == operationId && stored.UserId == userId && stored.Status == expected
                && stored.DraftVersion == expectedDraftVersion && stored.ExpiresAtUtc <= at)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(stored => stored.Status, expired)
                .SetProperty(stored => stored.UpdatedAtUtc, at), cancellationToken) == 1;
    }

    private IQueryable<HeaderRow> Headers(System.Linq.Expressions.Expression<Func<AssistantOperationRecord, bool>> filter) =>
        db.Set<AssistantOperationRecord>()
            .AsNoTracking()
            .Where(filter)
            .Select(operation => new HeaderRow(operation.Id, operation.Kind, operation.Status, operation.DraftVersion, operation.ExpiresAtUtc));

    private static AssistantOperationHeader ToHeader(HeaderRow row) =>
        new(row.Id, row.Kind, Enum.Parse<AssistantOperationStatus>(row.Status), row.DraftVersion, row.ExpiresAtUtc);

    private sealed record HeaderRow(Guid Id, string Kind, string Status, int DraftVersion, DateTimeOffset ExpiresAtUtc);
}
