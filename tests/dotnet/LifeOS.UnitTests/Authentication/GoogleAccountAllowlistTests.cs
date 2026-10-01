using LifeOS.Api.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace LifeOS.UnitTests.Authentication;

public class GoogleAccountAllowlistTests
{
    [Fact]
    public void Allows_OnlyListedVerifiedEmails_TrimmedAndCaseInsensitive()
    {
        var allowlist = Allowlist("Production", "  Person@Example.com ", "other@example.org");

        Assert.False(allowlist.IsOpen);
        Assert.True(allowlist.Allows("person@example.com", emailVerified: true));
        Assert.True(allowlist.Allows(" PERSON@EXAMPLE.COM ", emailVerified: true));
        Assert.True(allowlist.Allows("Other@Example.org", emailVerified: true));
        Assert.False(allowlist.Allows("person@example.com", emailVerified: false));
        Assert.False(allowlist.Allows("someone.else@example.com", emailVerified: true));
        Assert.False(allowlist.Allows("person@example.com.evil.example", emailVerified: true));
        Assert.False(allowlist.Allows(null, emailVerified: true));
        Assert.False(allowlist.Allows("", emailVerified: true));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OutsideDevelopment_AnEmptyOrMissingList_FailsStartup(string environment)
    {
        var missing = Assert.Throws<InvalidOperationException>(() => Allowlist(environment));
        var blank = Assert.Throws<InvalidOperationException>(() => Allowlist(environment, "", "   "));

        Assert.Contains(GoogleAccountAllowlist.AllowedEmailsKey, missing.Message);
        Assert.Contains(GoogleAccountAllowlist.AllowedEmailsKey, blank.Message);
    }

    [Fact]
    public void Development_WithoutAList_StaysOpen()
    {
        var allowlist = Allowlist("Development");

        Assert.True(allowlist.IsOpen);
        Assert.True(allowlist.Allows("anyone@example.com", emailVerified: false));
    }

    [Fact]
    public void Development_WithAList_IsEnforced()
    {
        var allowlist = Allowlist("Development", "person@example.com");

        Assert.False(allowlist.IsOpen);
        Assert.False(allowlist.Allows("someone.else@example.com", emailVerified: true));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("person@")]
    [InlineData("a@b@example.com")]
    [InlineData("person @example.com")]
    public void AnEntryThatIsNotAnEmail_FailsStartup_WithoutEchoingIt(string entry)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Allowlist("Production", "person@example.com", entry));

        Assert.DoesNotContain(entry.Trim(), exception.Message.Replace(GoogleAccountAllowlist.AllowedEmailsKey, ""));
    }

    [Fact]
    public void AppCallbacks_AreFixedByEnvironment()
    {
        var development = AppCallbacks.For(new TestEnvironment("Development"));
        var production = AppCallbacks.For(new TestEnvironment("Production"));

        Assert.Equal("lifeos-dev://auth", development.Auth);
        Assert.Equal("lifeos-dev://auth?error=sign_in_failed", development.SignInFailed);
        Assert.Equal("lifeos://auth", production.Auth);
        Assert.Equal("lifeos://auth?error=sign_in_failed", production.SignInFailed);
        Assert.True(production.IsAllowed("lifeos://auth"));
        Assert.False(production.IsAllowed("lifeos-dev://auth"));
        Assert.False(production.IsAllowed("LIFEOS://auth"));
        Assert.False(production.IsAllowed(null));
        Assert.False(development.IsAllowed("lifeos://auth"));
    }

    private static GoogleAccountAllowlist Allowlist(string environment, params string[] emails)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(emails.Select((email, index) =>
                new KeyValuePair<string, string?>($"{GoogleAccountAllowlist.AllowedEmailsKey}:{index}", email)))
            .Build();

        return GoogleAccountAllowlist.FromConfiguration(configuration, new TestEnvironment(environment));
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "LifeOS.Api";

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
