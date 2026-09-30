using LifeOS.Domain.Users;

namespace LifeOS.UnitTests.Users;

public class UserTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 30, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void CreateFromExternalIdentity_WithValidInput_ReturnsUser()
    {
        var user = User.CreateFromExternalIdentity("Test Person", "person@example.com", CreatedAtUtc);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal(7, user.Id.Version);
        Assert.Equal("Test Person", user.DisplayName);
        Assert.Equal("person@example.com", user.Email);
        Assert.Equal(CreatedAtUtc, user.CreatedAtUtc);
    }

    [Fact]
    public void CreateFromExternalIdentity_StartsWithIncompleteOnboarding()
    {
        var user = User.CreateFromExternalIdentity("Test Person", "person@example.com", CreatedAtUtc);

        Assert.Equal(OnboardingStatus.PendingFinanceProfile, user.OnboardingStatus);
        Assert.Null(user.DefaultCurrency);
        Assert.Null(user.StarterCategoriesInitializedAtUtc);
        Assert.Null(user.OnboardingCompletedAtUtc);
    }

    [Fact]
    public void CreateFromExternalIdentity_GeneratesDistinctIds()
    {
        var first = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);
        var second = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateFromExternalIdentity_WithoutDisplayNameOrEmail_StoresNull(string? value)
    {
        var user = User.CreateFromExternalIdentity(value, value, CreatedAtUtc);

        Assert.Null(user.DisplayName);
        Assert.Null(user.Email);
    }

    [Fact]
    public void CreateFromExternalIdentity_TrimsDisplayNameAndEmail()
    {
        var user = User.CreateFromExternalIdentity("  Test Person ", " person@example.com  ", CreatedAtUtc);

        Assert.Equal("Test Person", user.DisplayName);
        Assert.Equal("person@example.com", user.Email);
    }

    [Fact]
    public void CreateFromExternalIdentity_NormalizesCreationTimeToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 30, 12, 30, 0, TimeSpan.FromHours(2));

        var user = User.CreateFromExternalIdentity(null, null, local);

        Assert.Equal(TimeSpan.Zero, user.CreatedAtUtc.Offset);
        Assert.Equal(local.UtcDateTime, user.CreatedAtUtc.UtcDateTime);
    }

    // ---- Onboarding transitions ----

    private static readonly DateTimeOffset SetUpAtUtc = CreatedAtUtc.AddMinutes(5);
    private static readonly DateTimeOffset CompletedAtUtc = CreatedAtUtc.AddMinutes(10);

    [Fact]
    public void SetUpFinanceProfile_FromPendingFinanceProfile_MovesToPendingFirstAccount()
    {
        var user = NewUser();

        user.SetUpFinanceProfile("EUR", SetUpAtUtc);

        Assert.Equal(OnboardingStatus.PendingFirstAccount, user.OnboardingStatus);
        Assert.Equal("EUR", user.DefaultCurrency);
        Assert.Equal(SetUpAtUtc, user.StarterCategoriesInitializedAtUtc);
        Assert.Null(user.OnboardingCompletedAtUtc);
    }

    [Fact]
    public void SetUpFinanceProfile_NormalizesCurrency()
    {
        var user = NewUser();

        user.SetUpFinanceProfile("  eur ", SetUpAtUtc);

        Assert.Equal("EUR", user.DefaultCurrency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    [InlineData("€UR")]
    public void SetUpFinanceProfile_WithInvalidCurrency_ThrowsAndChangesNothing(string? currency)
    {
        var user = NewUser();

        Assert.ThrowsAny<ArgumentException>(() => user.SetUpFinanceProfile(currency!, SetUpAtUtc));

        Assert.Equal(OnboardingStatus.PendingFinanceProfile, user.OnboardingStatus);
        Assert.Null(user.DefaultCurrency);
        Assert.Null(user.StarterCategoriesInitializedAtUtc);
    }

    [Fact]
    public void SetUpFinanceProfile_StoresTimestampInUtc()
    {
        var user = NewUser();
        var local = new DateTimeOffset(2026, 9, 30, 12, 35, 0, TimeSpan.FromHours(2));

        user.SetUpFinanceProfile("EUR", local);

        Assert.Equal(TimeSpan.Zero, user.StarterCategoriesInitializedAtUtc!.Value.Offset);
        Assert.Equal(local.UtcDateTime, user.StarterCategoriesInitializedAtUtc.Value.UtcDateTime);
    }

    [Fact]
    public void SetUpFinanceProfile_WhenAlreadySetUp_Throws()
    {
        var user = NewUser();
        user.SetUpFinanceProfile("EUR", SetUpAtUtc);

        Assert.Throws<InvalidOperationException>(() => user.SetUpFinanceProfile("EUR", SetUpAtUtc));
    }

    [Fact]
    public void SetUpFinanceProfile_WhenCompleted_Throws()
    {
        var user = CompletedUser();

        Assert.Throws<InvalidOperationException>(() => user.SetUpFinanceProfile("USD", SetUpAtUtc));
        Assert.Equal("EUR", user.DefaultCurrency);
    }

    [Fact]
    public void CompleteOnboarding_FromPendingFirstAccount_MovesToCompleted()
    {
        var user = NewUser();
        user.SetUpFinanceProfile("EUR", SetUpAtUtc);

        user.CompleteOnboarding(CompletedAtUtc);

        Assert.Equal(OnboardingStatus.Completed, user.OnboardingStatus);
        Assert.Equal(CompletedAtUtc, user.OnboardingCompletedAtUtc);
        Assert.Equal("EUR", user.DefaultCurrency);
    }

    [Fact]
    public void CompleteOnboarding_StoresTimestampInUtc()
    {
        var user = NewUser();
        user.SetUpFinanceProfile("EUR", SetUpAtUtc);
        var local = new DateTimeOffset(2026, 9, 30, 12, 40, 0, TimeSpan.FromHours(2));

        user.CompleteOnboarding(local);

        Assert.Equal(TimeSpan.Zero, user.OnboardingCompletedAtUtc!.Value.Offset);
        Assert.Equal(local.UtcDateTime, user.OnboardingCompletedAtUtc.Value.UtcDateTime);
    }

    [Fact]
    public void CompleteOnboarding_FromPendingFinanceProfile_Throws()
    {
        var user = NewUser();

        Assert.Throws<InvalidOperationException>(() => user.CompleteOnboarding(CompletedAtUtc));
        Assert.Equal(OnboardingStatus.PendingFinanceProfile, user.OnboardingStatus);
        Assert.Null(user.OnboardingCompletedAtUtc);
    }

    [Fact]
    public void CompleteOnboarding_WhenAlreadyCompleted_Throws()
    {
        var user = CompletedUser();

        Assert.Throws<InvalidOperationException>(() => user.CompleteOnboarding(CompletedAtUtc.AddDays(1)));
        Assert.Equal(CompletedAtUtc, user.OnboardingCompletedAtUtc);
    }

    private static User NewUser() => User.CreateFromExternalIdentity(null, null, CreatedAtUtc);

    private static User CompletedUser()
    {
        var user = NewUser();
        user.SetUpFinanceProfile("EUR", SetUpAtUtc);
        user.CompleteOnboarding(CompletedAtUtc);

        return user;
    }
}
