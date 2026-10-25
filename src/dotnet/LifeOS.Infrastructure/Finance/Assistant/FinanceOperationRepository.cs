using LifeOS.Application.Finance.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Infrastructure.Assistant;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Infrastructure.Finance.Assistant;

// AI-007 F2 on PostgreSQL. Reads are owner-scoped and untracked. The insert is one statement that the partial
// unique index (one open operation per conversation) decides between concurrent requests, and the composite
// conversation foreign key rejects a conversation that is missing or another user's. Every change is one
// conditional UPDATE on (id, owner, status, draft_version); the confirmation additionally locks the row
// (SELECT ... FOR UPDATE) for the rest of its transaction. No statement here uses SaveChanges, so nothing
// tracked by the request's DbContext is written as a side effect.
internal sealed class FinanceOperationRepository(LifeOSDbContext db) : IFinanceOperationRepository
{
    private const string Kind = AssistantOperationKinds.FinanceTransactionCreate;

    public async Task<FinanceOperationAddOutcome> TryAddAsync(FinanceOperation operation, CancellationToken cancellationToken)
    {
        if (!operation.IsOpen || operation.DraftVersion != FinanceOperation.FirstDraftVersion)
        {
            throw new ArgumentException("Only a new open operation can be added.", nameof(operation));
        }

        var row = ToRecord(operation);

        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO assistant_operations
                    (id, user_id, conversation_id, kind, status, draft_version, payload_version, draft, expires_at_utc,
                     result_transaction_id, created_at_utc, updated_at_utc, executed_at_utc)
                VALUES
                    ({row.Id}, {row.UserId}, {row.ConversationId}, {row.Kind}, {row.Status}, {row.DraftVersion}, {row.PayloadVersion},
                     CAST({row.Draft} AS jsonb), {row.ExpiresAtUtc}, NULL, {row.CreatedAtUtc}, {row.UpdatedAtUtc}, NULL)
                ON CONFLICT (conversation_id) WHERE status IN ('Collecting', 'AwaitingConfirmation') DO NOTHING
                """, cancellationToken);

            return inserted == 1 ? FinanceOperationAddOutcome.Added : FinanceOperationAddOutcome.OpenOperationExists;
        }
        catch (Exception exception) when (PostgresErrors.IsForeignKeyViolation(exception, AssistantOperationConfiguration.ConversationForeignKeyName))
        {
            return FinanceOperationAddOutcome.ConversationNotFound;
        }
    }

    public async Task<FinanceOperation?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken)
    {
        var row = await Operations()
            .SingleOrDefaultAsync(operation => operation.Id == operationId && operation.UserId == userId, cancellationToken);

        return row is null ? null : ToDomain(row);
    }

    public async Task<FinanceOperation?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var collecting = nameof(AssistantOperationStatus.Collecting);
        var awaiting = nameof(AssistantOperationStatus.AwaitingConfirmation);

        var row = await Operations()
            .SingleOrDefaultAsync(operation => operation.ConversationId == conversationId && operation.UserId == userId
                && (operation.Status == collecting || operation.Status == awaiting), cancellationToken);

        return row is null ? null : ToDomain(row);
    }

    public async Task<bool> TryUpdateAsync(FinanceOperation operation, AssistantOperationStatus expectedStatus, int expectedDraftVersion,
        CancellationToken cancellationToken)
    {
        var row = ToRecord(operation);
        var expected = expectedStatus.ToString();

        return await db.Set<AssistantOperationRecord>()
            .Where(stored => stored.Id == row.Id && stored.UserId == row.UserId && stored.Kind == Kind
                && stored.Status == expected && stored.DraftVersion == expectedDraftVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(stored => stored.Status, row.Status)
                .SetProperty(stored => stored.DraftVersion, row.DraftVersion)
                .SetProperty(stored => stored.PayloadVersion, row.PayloadVersion)
                .SetProperty(stored => stored.Draft, row.Draft)
                .SetProperty(stored => stored.ExpiresAtUtc, row.ExpiresAtUtc)
                .SetProperty(stored => stored.ResultTransactionId, row.ResultTransactionId)
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

        var locked = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM assistant_operations
            WHERE id = {operationId} AND user_id = {userId} AND kind = {Kind}
              AND status = 'AwaitingConfirmation' AND draft_version = {expectedDraftVersion} AND expires_at_utc > {at}
            FOR UPDATE
            """).ToListAsync(cancellationToken);

        return locked.Count == 1;
    }

    private IQueryable<AssistantOperationRecord> Operations() =>
        db.Set<AssistantOperationRecord>().AsNoTracking().Where(operation => operation.Kind == Kind);

    private static AssistantOperationRecord ToRecord(FinanceOperation operation) => new()
    {
        Id = operation.Id,
        UserId = operation.UserId,
        ConversationId = operation.ConversationId,
        Kind = operation.Kind,
        Status = operation.Status.ToString(),
        DraftVersion = operation.DraftVersion,
        PayloadVersion = operation.PayloadVersion,
        Draft = FinanceTransactionDraftJson.Serialize(operation.PayloadVersion, operation.Draft),
        ExpiresAtUtc = operation.ExpiresAtUtc,
        ResultTransactionId = operation.ResultTransactionId,
        CreatedAtUtc = operation.CreatedAtUtc,
        UpdatedAtUtc = operation.UpdatedAtUtc,
        ExecutedAtUtc = operation.ExecutedAtUtc
    };

    private static FinanceOperation ToDomain(AssistantOperationRecord row) => FinanceOperation.Restore(
        row.Id,
        row.UserId,
        row.ConversationId,
        row.Kind,
        Enum.Parse<AssistantOperationStatus>(row.Status),
        row.DraftVersion,
        row.PayloadVersion,
        FinanceTransactionDraftJson.Deserialize(row.PayloadVersion, row.Draft),
        row.ExpiresAtUtc,
        row.ResultTransactionId,
        row.CreatedAtUtc,
        row.UpdatedAtUtc,
        row.ExecutedAtUtc);
}
