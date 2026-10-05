using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LifeOS.Api.Automation;

public sealed class AutomationKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public byte[] KeyHash { get; set; } = [];
}

// AUTO-001 §13: the scheduler's credential. A dedicated scheme used only by the tick policy, so the
// user pipeline (JWT bearer) is untouched and a user access token can never satisfy it. The key is
// compared as SHA-256 hashes in constant time and is never logged. Failure is a bare 401 (the
// default challenge: no body).
public sealed class AutomationKeyAuthenticationHandler(
    IOptionsMonitor<AutomationKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AutomationKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "LifeOSAutomationKey";
    public const string HeaderName = "X-LifeOS-Automation-Key";
    public const string ClaimType = "lifeos_automation";

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[HeaderName];

        if (presented.Count != 1 || string.IsNullOrEmpty(presented[0]))
        {
            Logger.LogWarning("Automation tick rejected: missing automation key.");
            return Task.FromResult(AuthenticateResult.Fail("Missing automation key."));
        }

        if (Options.KeyHash.Length == 0 || !CryptographicOperations.FixedTimeEquals(Hash(presented[0]!), Options.KeyHash))
        {
            Logger.LogWarning("Automation tick rejected: invalid automation key.");
            return Task.FromResult(AuthenticateResult.Fail("Invalid automation key."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimType, "tick")], SchemeName);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
