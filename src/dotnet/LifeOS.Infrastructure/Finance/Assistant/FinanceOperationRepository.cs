using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Infrastructure.Assistant;
using LifeOS.Infrastructure.Persistence;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Infrastructure.Finance.Assistant;

// AI-007 F2 on PostgreSQL: the finance.transaction.create operations. AI-008: the statements are the shared
// AssistantOperationRepository's (same SQL as before, kind-scoped), with the Finance draft document.
internal sealed class FinanceOperationRepository(LifeOSDbContext db) : IFinanceOperationRepository
{
    private readonly AssistantOperationRepository<FinanceTransactionDraft> _operations = new(db, FinanceTransactionDraftCodec.Instance);

    public async Task<FinanceOperationAddOutcome> TryAddAsync(FinanceOperation operation, CancellationToken cancellationToken) =>
        await _operations.TryAddAsync(operation, cancellationToken) switch
        {
            AssistantOperationAddOutcome.Added => FinanceOperationAddOutcome.Added,
            AssistantOperationAddOutcome.ConversationNotFound => FinanceOperationAddOutcome.ConversationNotFound,
            _ => FinanceOperationAddOutcome.OpenOperationExists
        };

    public Task<FinanceOperation?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken) =>
        _operations.GetAsync(userId, operationId, cancellationToken);

    public Task<FinanceOperation?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        _operations.GetOpenAsync(userId, conversationId, cancellationToken);

    public Task<bool> TryUpdateAsync(FinanceOperation operation, AssistantOperationStatus expectedStatus, int expectedDraftVersion,
        CancellationToken cancellationToken) =>
        _operations.TryUpdateAsync(operation, expectedStatus, expectedDraftVersion, cancellationToken);

    public Task<bool> TryLockForConfirmationAsync(Guid userId, Guid operationId, int expectedDraftVersion, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        _operations.TryLockForConfirmationAsync(userId, operationId, expectedDraftVersion, now, cancellationToken);
}

internal sealed class FinanceTransactionDraftCodec : IAssistantDraftCodec<FinanceTransactionDraft>
{
    public static FinanceTransactionDraftCodec Instance { get; } = new();

    public string Kind => AssistantOperationKinds.FinanceTransactionCreate;

    public string Serialize(int payloadVersion, FinanceTransactionDraft draft) => FinanceTransactionDraftJson.Serialize(payloadVersion, draft);

    public FinanceTransactionDraft Deserialize(int payloadVersion, string json) => FinanceTransactionDraftJson.Deserialize(payloadVersion, json);
}
