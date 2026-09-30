using LifeOS.Domain.Users;

namespace LifeOS.UnitTests.Users;

public class ExternalIdentityTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateTimeOffset SignedInAtUtc = new(2026, 9, 30, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithValidInput_ReturnsIdentity()
    {
        var identity = ExternalIdentity.Create(UserId, "google", "subject-A", "person@example.com", SignedInAtUtc);

        Assert.NotEqual(Guid.Empty, identity.Id);
        Assert.Equal(7, identity.Id.Version);
        Assert.Equal(UserId, identity.UserId);
        Assert.Equal("google", identity.Provider);
        Assert.Equal("subject-A", identity.Subject);
        Assert.Equal("person@example.com", identity.EmailAtSignIn);
        Assert.Equal(SignedInAtUtc, identity.CreatedAtUtc);
        Assert.Equal(SignedInAtUtc, identity.LastSignInAtUtc);
    }

    [Fact]
    public void Create_WithEmptyUserId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => ExternalIdentity.Create(Guid.Empty, "google", "subject-A", null, SignedInAtUtc));

        Assert.Equal("userId", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankProvider_Throws(string? provider)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ExternalIdentity.Create(UserId, provider!, "subject-A", null, SignedInAtUtc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankSubject_Throws(string? subject)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ExternalIdentity.Create(UserId, "google", subject!, null, SignedInAtUtc));
    }

    [Fact]
    public void Create_TrimsAndLowercasesProvider()
    {
        var identity = ExternalIdentity.Create(UserId, "  GooGle ", "subject-A", null, SignedInAtUtc);

        Assert.Equal("google", identity.Provider);
    }

    [Fact]
    public void Create_TrimsSubjectAndPreservesCase()
    {
        var identity = ExternalIdentity.Create(UserId, "google", "  Subject-A  ", null, SignedInAtUtc);

        Assert.Equal("Subject-A", identity.Subject);
    }

    [Fact]
    public void Create_WithProviderAtMaxLength_Succeeds()
    {
        var provider = new string('p', ExternalIdentity.MaxProviderLength);

        var identity = ExternalIdentity.Create(UserId, provider, "subject-A", null, SignedInAtUtc);

        Assert.Equal(provider, identity.Provider);
    }

    [Fact]
    public void Create_WithProviderTooLong_Throws()
    {
        var provider = new string('p', ExternalIdentity.MaxProviderLength + 1);

        var exception = Assert.Throws<ArgumentException>(
            () => ExternalIdentity.Create(UserId, provider, "subject-A", null, SignedInAtUtc));

        Assert.Equal("provider", exception.ParamName);
    }

    [Fact]
    public void Create_WithSubjectAtMaxLength_Succeeds()
    {
        var subject = new string('s', ExternalIdentity.MaxSubjectLength);

        var identity = ExternalIdentity.Create(UserId, "google", subject, null, SignedInAtUtc);

        Assert.Equal(subject, identity.Subject);
    }

    [Fact]
    public void Create_WithSubjectTooLong_Throws()
    {
        var subject = new string('s', ExternalIdentity.MaxSubjectLength + 1);

        var exception = Assert.Throws<ArgumentException>(
            () => ExternalIdentity.Create(UserId, "google", subject, null, SignedInAtUtc));

        Assert.Equal("subject", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutEmail_StoresNull(string? email)
    {
        var identity = ExternalIdentity.Create(UserId, "google", "subject-A", email, SignedInAtUtc);

        Assert.Null(identity.EmailAtSignIn);
    }

    [Fact]
    public void Create_TrimsEmail()
    {
        var identity = ExternalIdentity.Create(UserId, "google", "subject-A", "  person@example.com ", SignedInAtUtc);

        Assert.Equal("person@example.com", identity.EmailAtSignIn);
    }

    [Fact]
    public void Create_NormalizesTimestampsToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 30, 12, 30, 0, TimeSpan.FromHours(2));

        var identity = ExternalIdentity.Create(UserId, "google", "subject-A", null, local);

        Assert.Equal(TimeSpan.Zero, identity.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, identity.LastSignInAtUtc.Offset);
        Assert.Equal(local.UtcDateTime, identity.CreatedAtUtc.UtcDateTime);
    }

    [Fact]
    public void RecordSignIn_UpdatesLastSignInAndEmail()
    {
        var identity = ExternalIdentity.Create(UserId, "google", "subject-A", "old@example.com", SignedInAtUtc);
        var later = SignedInAtUtc.AddDays(1);

        identity.RecordSignIn(" new@example.com ", later);

        Assert.Equal("new@example.com", identity.EmailAtSignIn);
        Assert.Equal(later, identity.LastSignInAtUtc);
        Assert.Equal(SignedInAtUtc, identity.CreatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordSignIn_WithoutEmail_KeepsPreviousSnapshot(string? email)
    {
        var identity = ExternalIdentity.Create(UserId, "google", "subject-A", "old@example.com", SignedInAtUtc);

        identity.RecordSignIn(email, SignedInAtUtc.AddDays(1));

        Assert.Equal("old@example.com", identity.EmailAtSignIn);
    }

    [Fact]
    public void RecordSignIn_NormalizesTimeToUtc()
    {
        var identity = ExternalIdentity.Create(UserId, "google", "subject-A", null, SignedInAtUtc);
        var local = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));

        identity.RecordSignIn(null, local);

        Assert.Equal(TimeSpan.Zero, identity.LastSignInAtUtc.Offset);
        Assert.Equal(local.UtcDateTime, identity.LastSignInAtUtc.UtcDateTime);
    }
}
