using LifeOS.Application.Onboarding;
using LifeOS.Application.Onboarding.CompleteOnboarding;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Users;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Onboarding;

public class CompleteOnboardingHandlerTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUserRepository _users = new();
    private readonly InMemoryAccountRepository _accounts = new();

    [Fact]
    public async Task HandleAsync_FromPendingFinanceProfile_IsRejected()
    {
        var user = AddUser(setUp: false);
        AddAccount(user.Id);

        var result = await CompleteAsync(user.Id);

        Assert.Equal(OnboardingResultStatus.Invalid, result.Status);
        Assert.Equal("onboardingStatus", result.Field);
        Assert.Equal(OnboardingStatus.PendingFinanceProfile, _users.Stored(user.Id).OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_WithoutAccount_IsRejected()
    {
        var user = AddUser(setUp: true);

        var result = await CompleteAsync(user.Id);

        Assert.Equal(OnboardingResultStatus.Invalid, result.Status);
        Assert.Equal("account", result.Field);
        Assert.Equal(OnboardingStatus.PendingFirstAccount, _users.Stored(user.Id).OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_AnotherUsersAccount_DoesNotCount()
    {
        var user = AddUser(setUp: true);
        var other = AddUser(setUp: true);
        AddAccount(other.Id);

        var result = await CompleteAsync(user.Id);

        Assert.Equal(OnboardingResultStatus.Invalid, result.Status);
        Assert.Equal("account", result.Field);
    }

    [Fact]
    public async Task HandleAsync_WithOwnAccount_CompletesOnboarding()
    {
        var user = AddUser(setUp: true);
        AddAccount(user.Id);

        var result = await CompleteAsync(user.Id);

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(OnboardingStatus.Completed, result.User!.OnboardingStatus);
        var stored = _users.Stored(user.Id);
        Assert.Equal(OnboardingStatus.Completed, stored.OnboardingStatus);
        Assert.Equal(UtcNow, stored.OnboardingCompletedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyCompleted_SucceedsWithoutWriting()
    {
        var user = AddUser(setUp: true);
        AddAccount(user.Id);
        await CompleteAsync(user.Id);

        var retry = await CompleteAsync(user.Id, UtcNow.AddHours(1));

        Assert.Equal(OnboardingResultStatus.Ok, retry.Status);
        Assert.Equal(1, _users.OnboardingUpdates);
        Assert.Equal(UtcNow, _users.Stored(user.Id).OnboardingCompletedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentCompletion_Succeeds()
    {
        var user = AddUser(setUp: true);
        AddAccount(user.Id);
        _users.BeforeUpdateOnboarding = () =>
        {
            _users.BeforeUpdateOnboarding = null;
            _users.Stored(user.Id).CompleteOnboarding(UtcNow);
        };

        var result = await CompleteAsync(user.Id);

        Assert.Equal(OnboardingResultStatus.Ok, result.Status);
        Assert.Equal(OnboardingStatus.Completed, result.User!.OnboardingStatus);
    }

    [Fact]
    public async Task HandleAsync_WithUnknownUser_ReturnsNotFound()
    {
        var result = await CompleteAsync(Guid.CreateVersion7());

        Assert.Equal(OnboardingResultStatus.NotFound, result.Status);
    }

    private Task<OnboardingResult> CompleteAsync(Guid userId, DateTimeOffset? now = null) =>
        new CompleteOnboardingHandler(_users, _accounts, new FixedTimeProvider(now ?? UtcNow))
            .HandleAsync(userId, CancellationToken.None);

    private User AddUser(bool setUp)
    {
        var user = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);

        if (setUp)
        {
            user.SetUpFinanceProfile("EUR", CreatedAtUtc);
        }

        _users.Users.Add(user);

        return user;
    }

    private void AddAccount(Guid userId) =>
        _accounts.Accounts.Add(Account.Create(userId, "Checking", AccountType.BankAccount, "EUR", CreatedAtUtc));
}
