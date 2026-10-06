using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Users;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Users;

namespace LifeOS.IntegrationTests.PostgreSql;

// Test-only helpers that make a race deterministic: each participant pauses at one point until
// every participant has reached it, then all continue concurrently against the real database.
internal sealed class Rendezvous
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly int _participants;
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public Rendezvous(int participants)
    {
        _participants = participants;
    }

    public async Task ArriveAndWaitAsync()
    {
        if (Interlocked.Increment(ref _arrived) == _participants)
        {
            _allArrived.SetResult();
        }

        // Fails instead of hanging if a participant never arrives.
        await _allArrived.Task.WaitAsync(Timeout);
    }
}

// Delegates to the real repository; the first identity lookup of this participant waits at the
// rendezvous, after it has seen "no identity yet". All participants then insert concurrently.
internal sealed class PausingUserRepository(IUserRepository inner, Rendezvous rendezvous) : IUserRepository
{
    private bool _paused;

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        inner.GetByIdAsync(userId, cancellationToken);

    public async Task<ExternalIdentity?> GetExternalIdentityAsync(string provider, string subject, CancellationToken cancellationToken)
    {
        var identity = await inner.GetExternalIdentityAsync(provider, subject, cancellationToken);

        if (!_paused)
        {
            _paused = true;
            await rendezvous.ArriveAndWaitAsync();
        }

        return identity;
    }

    public Task<bool> TryAddAsync(User user, ExternalIdentity identity, CancellationToken cancellationToken) =>
        inner.TryAddAsync(user, identity, cancellationToken);

    public Task UpdateExternalIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken) =>
        inner.UpdateExternalIdentityAsync(identity, cancellationToken);

    public Task<bool> UpdateTimeZoneAsync(Guid userId, string timeZoneId, CancellationToken cancellationToken) =>
        inner.UpdateTimeZoneAsync(userId, timeZoneId, cancellationToken);

    public Task<bool> TryUpdateOnboardingAsync(User user, OnboardingStatus expectedStatus, CancellationToken cancellationToken) =>
        inner.TryUpdateOnboardingAsync(user, expectedStatus, cancellationToken);
}

// Delegates to the real repository; the first category read of this participant waits at the
// rendezvous, so every participant computes its missing starter set from the same empty state.
internal sealed class PausingCategoryRepository(ICategoryRepository inner, Rendezvous rendezvous) : ICategoryRepository
{
    private bool _paused;

    public Task<bool> TryAddAsync(Category category, CancellationToken cancellationToken) =>
        inner.TryAddAsync(category, cancellationToken);

    public Task<bool> TryAddRangeAsync(IReadOnlyCollection<Category> categories, CancellationToken cancellationToken) =>
        inner.TryAddRangeAsync(categories, cancellationToken);

    public Task<Category?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        inner.GetByIdAsync(userId, id, cancellationToken);

    public Task<IReadOnlyList<Category>> GetByTypeAndParentAsync(
        Guid userId,
        CategoryType categoryType,
        Guid? parentCategoryId,
        CancellationToken cancellationToken) =>
        inner.GetByTypeAndParentAsync(userId, categoryType, parentCategoryId, cancellationToken);

    public Task<CategoryRenameOutcome> TryRenameAsync(Category category, CancellationToken cancellationToken) =>
        inner.TryRenameAsync(category, cancellationToken);

    public Task<CategoryDeleteOutcome> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken) =>
        inner.DeleteAsync(userId, categoryId, cancellationToken);

    public async Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        var categories = await inner.GetAllAsync(userId, cancellationToken);

        if (!_paused)
        {
            _paused = true;
            await rendezvous.ArriveAndWaitAsync();
        }

        return categories;
    }
}
