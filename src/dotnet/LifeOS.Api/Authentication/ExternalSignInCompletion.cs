using LifeOS.Application.Users.SignInWithExternalIdentity;
using Microsoft.AspNetCore.WebUtilities;

namespace LifeOS.Api.Authentication;

// An identity already verified by an external provider (e.g. Google), in provider-neutral form.
// EmailVerified: the provider confirmed the address belongs to this account.
public sealed record VerifiedExternalIdentity(
    string Provider,
    string Subject,
    string? Email,
    bool EmailVerified,
    string? DisplayName);

// Completes an external sign-in: checks the account allowlist, resolves or creates the LifeOS user
// and issues a one-time authorization code for the app. No LifeOS session exists yet; it is created
// only when the app exchanges the code with its PKCE verifier (POST /api/auth/token).
public sealed class ExternalSignInCompletion
{
    private readonly SignInWithExternalIdentityHandler _signInHandler;
    private readonly AuthorizationCodeStore _codes;
    private readonly AppCallbacks _callbacks;
    private readonly GoogleAccountAllowlist _allowlist;
    private readonly ILogger<ExternalSignInCompletion> _logger;

    public ExternalSignInCompletion(
        SignInWithExternalIdentityHandler signInHandler,
        AuthorizationCodeStore codes,
        AppCallbacks callbacks,
        GoogleAccountAllowlist allowlist,
        ILogger<ExternalSignInCompletion> logger)
    {
        _signInHandler = signInHandler;
        _codes = codes;
        _callbacks = callbacks;
        _allowlist = allowlist;
        _logger = logger;
    }

    // Returns the app callback URI carrying only the one-time code, or only the sign-in error when
    // the account is not allowed (no user, identity, code or session is created for it).
    public async Task<string> CompleteAsync(
        VerifiedExternalIdentity identity,
        string codeChallenge,
        CancellationToken cancellationToken)
    {
        if (!Pkce.IsValidS256Challenge(codeChallenge))
        {
            throw new InvalidOperationException("The PKCE challenge is not valid.");
        }

        // Google is the only external provider, so its allowlist applies to every external sign-in.
        if (!_allowlist.Allows(identity.Email, identity.EmailVerified))
        {
            // Deliberately without the email or subject.
            _logger.LogWarning("External sign-in refused: the {Provider} account is not allowed.", identity.Provider);

            return _callbacks.SignInFailed;
        }

        var signIn = await _signInHandler.HandleAsync(
            new SignInWithExternalIdentityCommand(identity.Provider, identity.Subject, identity.Email, identity.DisplayName),
            cancellationToken);

        var code = _codes.Create(signIn.UserId, codeChallenge, _callbacks.Auth);

        return QueryHelpers.AddQueryString(_callbacks.Auth, "code", code);
    }
}

// The one app callback URI LifeOS redirects to after an external sign-in. It is fixed by the API's
// environment, never taken from the request: the Development API serves the development app
// (it.colazzo.lifeos.dev, lifeos-dev://auth), every other environment the production app
// (it.colazzo.lifeos, lifeos://auth). A redirect_uri sent by the app must match it exactly.
public sealed class AppCallbacks
{
    public const string Development = "lifeos-dev://auth";
    public const string Production = "lifeos://auth";

    private AppCallbacks(string auth)
    {
        Auth = auth;
    }

    public string Auth { get; }

    public string SignInFailed => Auth + "?error=sign_in_failed";

    public static AppCallbacks For(IHostEnvironment environment) =>
        new(environment.IsDevelopment() ? Development : Production);

    public bool IsAllowed(string? redirectUri) => string.Equals(redirectUri, Auth, StringComparison.Ordinal);
}
