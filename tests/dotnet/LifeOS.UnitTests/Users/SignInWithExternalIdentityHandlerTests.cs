using LifeOS.Application.Users.SignInWithExternalIdentity;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Users;

public class SignInWithExternalIdentityHandlerTests
{
    private static readonly DateTimeOffset FirstSignInUtc = new(2026, 9, 30, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LaterSignInUtc = FirstSignInUtc.AddDays(3);

    private readonly InMemoryUserRepository _repository = new();

    [Fact]
    public async Task HandleAsync_WithNewIdentity_CreatesUserAndIdentity()
    {
        var result = await SignInAsync(FirstSignInUtc, "google", "subject-A", "person@example.com", "Test Person");

        var user = Assert.Single(_repository.Users);
        var identity = Assert.Single(_repository.Identities);
        Assert.True(result.IsNewUser);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(user.Id, identity.UserId);
        Assert.Equal("google", identity.Provider);
        Assert.Equal("subject-A", identity.Subject);
    }

    [Fact]
    public async Task HandleAsync_WithNewIdentity_InitializesProfileFromProvider()
    {
        await SignInAsync(FirstSignInUtc, "google", "subject-A", " person@example.com ", " Test Person ");

        var user = Assert.Single(_repository.Users);
        var identity = Assert.Single(_repository.Identities);
        Assert.Equal("Test Person", user.DisplayName);
        Assert.Equal("person@example.com", user.Email);
        Assert.Equal("person@example.com", identity.EmailAtSignIn);
    }

    [Fact]
    public async Task HandleAsync_WithNewIdentity_UsesCurrentTimeFromTimeProvider()
    {
        await SignInAsync(FirstSignInUtc, "google", "subject-A", null, null);

        var user = Assert.Single(_repository.Users);
        var identity = Assert.Single(_repository.Identities);
        Assert.Equal(FirstSignInUtc, user.CreatedAtUtc);
        Assert.Equal(FirstSignInUtc, identity.CreatedAtUtc);
        Assert.Equal(FirstSignInUtc, identity.LastSignInAtUtc);
    }

    [Fact]
    public async Task HandleAsync_WithKnownIdentity_ReturnsSameUserWithoutCreatingAnother()
    {
        var first = await SignInAsync(FirstSignInUtc, "google", "subject-A", "person@example.com", "Test Person");

        var second = await SignInAsync(LaterSignInUtc, "google", "subject-A", "person@example.com", "Test Person");

        Assert.False(second.IsNewUser);
        Assert.Equal(first.UserId, second.UserId);
        Assert.Single(_repository.Users);
        Assert.Single(_repository.Identities);
    }

    [Fact]
    public async Task HandleAsync_WithKnownIdentity_UpdatesLastSignInAndPersists()
    {
        await SignInAsync(FirstSignInUtc, "google", "subject-A", null, null);

        await SignInAsync(LaterSignInUtc, "google", "subject-A", null, null);

        var updated = Assert.Single(_repository.UpdatedIdentities);
        Assert.Equal(LaterSignInUtc, updated.LastSignInAtUtc);
        Assert.Equal(FirstSignInUtc, updated.CreatedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_WithKnownIdentityAndChangedEmail_UpdatesEmailSnapshot()
    {
        await SignInAsync(FirstSignInUtc, "google", "subject-A", "old@example.com", null);

        await SignInAsync(LaterSignInUtc, "google", "subject-A", "new@example.com", null);

        var updated = Assert.Single(_repository.UpdatedIdentities);
        Assert.Equal("new@example.com", updated.EmailAtSignIn);
    }

    [Fact]
    public async Task HandleAsync_WithKnownIdentityAndNoEmail_KeepsEmailSnapshot()
    {
        await SignInAsync(FirstSignInUtc, "google", "subject-A", "old@example.com", null);

        await SignInAsync(LaterSignInUtc, "google", "subject-A", null, null);

        var updated = Assert.Single(_repository.UpdatedIdentities);
        Assert.Equal("old@example.com", updated.EmailAtSignIn);
    }

    [Fact]
    public async Task HandleAsync_WithKnownIdentity_DoesNotChangeUserProfile()
    {
        await SignInAsync(FirstSignInUtc, "google", "subject-A", "old@example.com", "Original Name");

        await SignInAsync(LaterSignInUtc, "google", "subject-A", "new@example.com", "Changed Name");

        var user = Assert.Single(_repository.Users);
        Assert.Equal("Original Name", user.DisplayName);
        Assert.Equal("old@example.com", user.Email);
    }

    [Fact]
    public async Task HandleAsync_SameEmailWithDifferentSubject_CreatesDifferentUsers()
    {
        var first = await SignInAsync(FirstSignInUtc, "google", "subject-A", "person@example.com", null);

        var second = await SignInAsync(LaterSignInUtc, "google", "subject-B", "person@example.com", null);

        Assert.True(second.IsNewUser);
        Assert.NotEqual(first.UserId, second.UserId);
        Assert.Equal(2, _repository.Users.Count);
    }

    [Fact]
    public async Task HandleAsync_DifferentEmailWithSameSubject_ReturnsSameUser()
    {
        var first = await SignInAsync(FirstSignInUtc, "google", "subject-A", "first@example.com", null);

        var second = await SignInAsync(LaterSignInUtc, "google", "subject-A", "second@example.com", null);

        Assert.False(second.IsNewUser);
        Assert.Equal(first.UserId, second.UserId);
    }

    [Fact]
    public async Task HandleAsync_SameSubjectWithDifferentProvider_CreatesDifferentUsers()
    {
        var google = await SignInAsync(FirstSignInUtc, "google", "subject-A", null, null);

        var dev = await SignInAsync(LaterSignInUtc, "dev", "subject-A", null, null);

        Assert.True(dev.IsNewUser);
        Assert.NotEqual(google.UserId, dev.UserId);
    }

    [Fact]
    public async Task HandleAsync_NormalizesProviderBeforeLookup()
    {
        var first = await SignInAsync(FirstSignInUtc, "google", "subject-A", null, null);

        var second = await SignInAsync(LaterSignInUtc, "  Google ", " subject-A ", null, null);

        Assert.False(second.IsNewUser);
        Assert.Equal(first.UserId, second.UserId);
    }

    [Fact]
    public async Task HandleAsync_SubjectsDifferingOnlyByCase_AreDifferentIdentities()
    {
        var lower = await SignInAsync(FirstSignInUtc, "google", "subject-a", null, null);

        var upper = await SignInAsync(LaterSignInUtc, "google", "SUBJECT-A", null, null);

        Assert.True(upper.IsNewUser);
        Assert.NotEqual(lower.UserId, upper.UserId);
    }

    [Theory]
    [InlineData("", "subject-A")]
    [InlineData("google", " ")]
    public async Task HandleAsync_WithBlankProviderOrSubject_ThrowsAndPersistsNothing(string provider, string subject)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => SignInAsync(FirstSignInUtc, provider, subject, null, null));

        Assert.Empty(_repository.Users);
        Assert.Empty(_repository.Identities);
    }

    private Task<SignInWithExternalIdentityResult> SignInAsync(
        DateTimeOffset utcNow,
        string provider,
        string subject,
        string? email,
        string? displayName)
    {
        var handler = new SignInWithExternalIdentityHandler(_repository, new FixedTimeProvider(utcNow));

        return handler.HandleAsync(
            new SignInWithExternalIdentityCommand(provider, subject, email, displayName),
            CancellationToken.None);
    }
}
