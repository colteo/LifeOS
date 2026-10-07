using LifeOS.Application.ActionAgent;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.ActionAgent;

namespace LifeOS.UnitTests.Fakes;

// Same rules as the PostgreSQL repository under one lock: owner-scoped reads, inserts only for an
// existing review without an open (Pending/Approved) proposal, status changes only from the expected
// status, payload never replaced. The SQL itself is proven against PostgreSQL in the integration tests.
internal sealed class InMemoryProposedActionRepository(IWeeklyReviewRepository? reviews = null) : IProposedActionRepository
{
    private readonly Lock _lock = new();

    public List<ProposedAction> Proposals { get; } = [];

    public int StatusUpdates { get; private set; }

    public Task<ProposedAction?> GetAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Proposals.SingleOrDefault(proposal => proposal.Id == proposalId && proposal.UserId == userId));
        }
    }

    public Task<ProposedAction?> GetLatestForReviewAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Proposals
                .Where(proposal => proposal.ReviewId == reviewId && proposal.UserId == userId)
                .OrderByDescending(proposal => proposal.CreatedAtUtc)
                .ThenByDescending(proposal => proposal.Id)
                .FirstOrDefault());
        }
    }

    public async Task<bool> TryAddAsync(ProposedAction proposal, CancellationToken cancellationToken)
    {
        if (proposal.Status != ProposedActionStatus.Pending)
        {
            throw new ArgumentException("Only a Pending proposal can be added.", nameof(proposal));
        }

        if (reviews is not null && await reviews.GetAsync(proposal.UserId, proposal.ReviewId, cancellationToken) is null)
        {
            return false;
        }

        lock (_lock)
        {
            if (Proposals.Any(existing => existing.ReviewId == proposal.ReviewId && existing.IsOpen))
            {
                return false;
            }

            Proposals.Add(proposal);
            return true;
        }
    }

    public Task<bool> TryUpdateStatusAsync(ProposedAction updated, ProposedActionStatus expected, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = Proposals.FindIndex(proposal => proposal.Id == updated.Id && proposal.UserId == updated.UserId && proposal.Status == expected);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            var stored = Proposals[index];

            if (stored.Payload != updated.Payload || stored.Rationale != updated.Rationale)
            {
                throw new InvalidOperationException("A status update must not change the payload.");
            }

            Proposals[index] = updated;
            StatusUpdates++;
            return Task.FromResult(true);
        }
    }
}
