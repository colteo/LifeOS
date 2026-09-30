using LifeOS.Application.Users.SignInWithExternalIdentity;
using Microsoft.AspNetCore.WebUtilities;

namespace LifeOS.Api.Authentication;

// An identity already verified by an external provider (e.g. Google), in provider-neutral form.
public sealed record VerifiedExternalIdentity(string Provider, string Subject, string? Email, string? DisplayName);

// Completes an external sign-in: resolves or creates the LifeOS user and issues a one-time
// authorization code for the app. No LifeOS session exists yet; it is created only when the app
// exchanges the code with its PKCE verifier (POST /api/auth/token).
public sealed class ExternalSignInCompletion
{
    private readonly SignInWithExternalIdentityHandler _signInHandler;
    private readonly AuthorizationCodeStore _codes;

    public ExternalSignInCompletion(SignInWithExternalIdentityHandler signInHandler, AuthorizationCodeStore codes)
    {
        _signInHandler = signInHandler;
        _codes = codes;
    }

    // Returns the app callback URI carrying only the one-time code.
    public async Task<string> CompleteAsync(
        VerifiedExternalIdentity identity,
        string codeChallenge,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        if (!AppCallbacks.IsAllowed(redirectUri) || !Pkce.IsValidS256Challenge(codeChallenge))
        {
            throw new InvalidOperationException("The sign-in callback or PKCE challenge is not valid.");
        }

        var signIn = await _signInHandler.HandleAsync(
            new SignInWithExternalIdentityCommand(identity.Provider, identity.Subject, identity.Email, identity.DisplayName),
            cancellationToken);

        var code = _codes.Create(signIn.UserId, codeChallenge, redirectUri);

        return QueryHelpers.AddQueryString(redirectUri, "code", code);
    }
}

// The app callback URIs LifeOS may redirect to after an external sign-in. Exact match only.
public static class AppCallbacks
{
    public const string Auth = "lifeos://auth";

    public static bool IsAllowed(string? redirectUri) => string.Equals(redirectUri, Auth, StringComparison.Ordinal);
}
