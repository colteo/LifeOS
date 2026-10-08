using LifeOS.Application.ActionAgent;
using LifeOS.Domain.ActionAgent;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.ActionAgent;

// AI-002 on PostgreSQL. Reads are owner-scoped. The insert is one statement that the partial unique
// index ux_proposed_actions_review_open decides between concurrent runs; status changes are one
// conditional UPDATE each (the row lock serializes concurrent decisions/executions), and none of them
// can touch the payload columns.
internal sealed class ProposedActionRepository(LifeOSDbContext db) : IProposedActionRepository
{
    public async Task<ProposedAction?> GetAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken)
    {
        var row = await db.Set<ProposedActionRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(proposal => proposal.Id == proposalId && proposal.UserId == userId, cancellationToken);

        return row?.ToDomain();
    }

    public async Task<ProposedAction?> GetLatestForReviewAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        var row = await db.Set<ProposedActionRecord>()
            .AsNoTracking()
            .Where(proposal => proposal.ReviewId == reviewId && proposal.UserId == userId)
            .OrderByDescending(proposal => proposal.CreatedAtUtc)
            .ThenByDescending(proposal => proposal.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return row?.ToDomain();
    }

    public async Task<bool> TryAddAsync(ProposedAction proposal, CancellationToken cancellationToken)
    {
        // Only new proposals are added: Pending, without decision, execution or failure.
        if (proposal.Status != ProposedActionStatus.Pending)
        {
            throw new ArgumentException("Only a Pending proposal can be added.", nameof(proposal));
        }

        var row = ProposedActionRecord.From(proposal);

        try
        {
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO proposed_actions
                    (id, user_id, review_id, action_type, status, payload_version, payload, rationale, provider, model,
                     prompt_version, tool_schema_version, tool_calls, step_count, created_at_utc, decided_at_utc,
                     executed_at_utc, failure_code)
                VALUES
                    ({row.Id}, {row.UserId}, {row.ReviewId}, {row.ActionType}, {row.Status}, {row.PayloadVersion},
                     CAST({row.Payload} AS jsonb), {row.Rationale}, {row.Provider}, {row.Model}, {row.PromptVersion},
                     {row.ToolSchemaVersion}, {row.ToolCalls}, {row.StepCount}, {row.CreatedAtUtc}, NULL, NULL, NULL)
                ON CONFLICT (review_id) WHERE status IN ('Pending', 'Approved') DO NOTHING
                """, cancellationToken) == 1;
        }
        catch (Exception exception) when (PostgresErrors.IsForeignKeyViolation(exception,
            ProposedActionConfiguration.ReviewForeignKeyName, ProposedActionConfiguration.UserForeignKeyName))
        {
            // The review (or its user) was deleted meanwhile.
            return false;
        }
    }

    public async Task<bool> TryUpdateStatusAsync(ProposedAction updated, ProposedActionStatus expected, CancellationToken cancellationToken)
    {
        var status = updated.Status.ToString();
        var expectedStatus = expected.ToString();

        return await db.Set<ProposedActionRecord>()
            .Where(proposal => proposal.Id == updated.Id && proposal.UserId == updated.UserId && proposal.Status == expectedStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(proposal => proposal.Status, status)
                .SetProperty(proposal => proposal.DecidedAtUtc, updated.DecidedAtUtc)
                .SetProperty(proposal => proposal.ExecutedAtUtc, updated.ExecutedAtUtc)
                .SetProperty(proposal => proposal.FailureCode, updated.FailureCode), cancellationToken) == 1;
    }
}
