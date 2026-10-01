using LifeOS.Application.Authentication.RefreshSession;
using LifeOS.Application.Authentication.RevokeSession;
using LifeOS.Application.Authentication.StartSession;
using LifeOS.Application.Users.SignInWithExternalIdentity;
using LifeOS.Contracts.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Authentication;

// Authorization is declared per endpoint. These endpoints are anonymous because the caller has
// no access token yet (sign-in) or proves possession of a refresh token instead (refresh, logout).
public static class AuthEndpoints
{
    private const int MaxAuthorizationCodeLength = 256;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints, bool developmentSignInEnabled)
    {
        var auth = endpoints.MapGroup("/api/auth")
            .AddEndpointFilter(async (context, next) =>
            {
                // Token responses must never be cached.
                context.HttpContext.Response.Headers.CacheControl = "no-store";

                return await next(context);
            });

        if (developmentSignInEnabled)
        {
            auth.MapPost("/dev/sign-in", DevSignInAsync)
                .WithName("DevSignIn")
                .AllowAnonymous();
        }

        auth.MapPost("/token", ExchangeCodeAsync)
            .WithName("ExchangeAuthorizationCode")
            .AllowAnonymous();

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
            .AllowAnonymous();

        auth.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .AllowAnonymous();

        return endpoints;
    }

    public static async Task<Results<Ok<TokenResponse>, ValidationProblem>> DevSignInAsync(
        DevSignInRequest request,
        SignInWithExternalIdentityHandler signInHandler,
        StartSessionHandler startSessionHandler,
        AccessTokenIssuer accessTokenIssuer,
        CancellationToken cancellationToken)
    {
        SignInWithExternalIdentityResult signIn;

        try
        {
            signIn = await signInHandler.HandleAsync(
                new SignInWithExternalIdentityCommand(
                    DevelopmentSignIn.Provider,
                    request.Subject ?? string.Empty,
                    request.Email,
                    request.DisplayName),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["subject"] = [exception.Message]
            });
        }

        var session = await startSessionHandler.HandleAsync(signIn.UserId, cancellationToken);

        return TypedResults.Ok(CreateTokenResponse(accessTokenIssuer, session.UserId, session.RefreshToken));
    }

    // Exchanges a one-time authorization code (from an external sign-in) plus its PKCE verifier for a
    // LifeOS session. The code is consumed before the verifier is checked, so a wrong verifier also
    // burns it. Unknown, expired, used or mismatched codes are indistinguishable (401).
    public static async Task<Results<Ok<TokenResponse>, ValidationProblem, ProblemHttpResult>> ExchangeCodeAsync(
        TokenExchangeRequest request,
        AuthorizationCodeStore codes,
        StartSessionHandler startSessionHandler,
        AccessTokenIssuer accessTokenIssuer,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > MaxAuthorizationCodeLength)
        {
            errors["code"] = ["An authorization code is required."];
        }

        if (!Pkce.IsValidVerifier(request.CodeVerifier))
        {
            errors["codeVerifier"] = ["A PKCE code verifier of 43-128 unreserved characters is required."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var grant = codes.TryConsume(request.Code!);

        if (grant is null || !Pkce.Matches(request.CodeVerifier!, grant.CodeChallenge))
        {
            return TypedResults.Problem(
                title: "Invalid authorization code.",
                detail: "Sign in again.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var session = await startSessionHandler.HandleAsync(grant.UserId, cancellationToken);

        return TypedResults.Ok(CreateTokenResponse(accessTokenIssuer, session.UserId, session.RefreshToken));
    }

    public static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> RefreshAsync(
        RefreshTokenRequest request,
        RefreshSessionHandler handler,
        AccessTokenIssuer accessTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.RefreshToken, cancellationToken);

        if (result.RevokedFamilyId is { } familyId)
        {
            // Never the token or its hash.
            loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogWarning(
                "Refresh token reuse detected: session family {SessionFamilyId} revoked.", familyId);
        }

        if (result.Status != RefreshSessionStatus.Refreshed)
        {
            return TypedResults.Problem(
                title: "Invalid refresh token.",
                detail: "Sign in again.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return TypedResults.Ok(CreateTokenResponse(accessTokenIssuer, result.UserId!.Value, result.RefreshToken!));
    }

    // Always 204, whether or not the token was known.
    public static async Task<NoContent> LogoutAsync(
        LogoutRequest request,
        RevokeSessionHandler handler,
        CancellationToken cancellationToken)
    {
        await handler.HandleAsync(request.RefreshToken, cancellationToken);

        return TypedResults.NoContent();
    }

    private static TokenResponse CreateTokenResponse(AccessTokenIssuer issuer, Guid userId, string refreshToken) =>
        new(issuer.Issue(userId), refreshToken, (int)issuer.Lifetime.TotalSeconds);
}
