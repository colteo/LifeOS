using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace LifeOS.Api.Authentication;

// Google sign-in for the app (ADR-006). The API is Google's OAuth client; the client secret never
// leaves the server and Google's tokens are neither stored nor passed on.
//
//   app → GET /api/auth/google/start?code_challenge=…&redirect_uri=<app callback>
//       → Google → /signin-google (ASP.NET handler) → GET /api/auth/google/complete
//       → <app callback>?code=<one-time code> → app → POST /api/auth/token
//
// The app callback is fixed by the environment (AppCallbacks): lifeos-dev://auth in Development,
// lifeos://auth everywhere else; the redirect_uri sent to /start must match it exactly. The PKCE
// challenge validated at /start travels only in the handler's protected state; /complete never
// takes it from the request. Only allowlisted, verified Google accounts complete (GoogleAccountAllowlist).
public static class GoogleSignIn
{
    public const string Provider = "google";
    public const string ExternalScheme = "LifeOS.External";

    private const string ClientIdKey = "Authentication:Google:ClientId";
    private const string ClientSecretKey = "Authentication:Google:ClientSecret";
    private const string CodeChallengeItem = "lifeos.code_challenge";
    private const string CompletePath = "/api/auth/google/complete";
    private const string EmailVerifiedClaim = "email_verified";

    // Enabled when both Google credentials are configured (User Secrets in development).
    // Only one of them is a configuration error. Outside Development Google sign-in is the only way
    // in, so the credentials are required there.
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment)
    {
        var hasClientId = !string.IsNullOrWhiteSpace(configuration[ClientIdKey]);
        var hasClientSecret = !string.IsNullOrWhiteSpace(configuration[ClientSecretKey]);

        if (hasClientId != hasClientSecret)
        {
            throw new InvalidOperationException($"Configure both {ClientIdKey} and {ClientSecretKey}, or neither.");
        }

        if (!hasClientId && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{ClientIdKey} and {ClientSecretKey} are required in the '{environment.EnvironmentName}' environment.");
        }

        return hasClientId;
    }

    public static AuthenticationBuilder AddLifeOSGoogle(this AuthenticationBuilder builder, IConfiguration configuration)
    {
        return builder
            // Carries the Google result from /signin-google to /complete only; never authenticates API calls.
            .AddCookie(ExternalScheme, options =>
            {
                options.Cookie.Name = "LifeOS.External";
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddGoogle(options =>
            {
                options.ClientId = configuration[ClientIdKey]!;
                options.ClientSecret = configuration[ClientSecretKey]!;
                options.SignInScheme = ExternalScheme;
                options.SaveTokens = false;

                // Google's userinfo "email_verified": the allowlist matches verified addresses only.
                options.ClaimActions.MapJsonKey(EmailVerifiedClaim, EmailVerifiedClaim);

                options.Events.OnRemoteFailure = context =>
                {
                    var services = context.HttpContext.RequestServices;

                    services
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(GoogleSignIn))
                        .LogWarning("Google sign-in failed: {Reason}", context.Failure?.Message);

                    context.Response.Redirect(services.GetRequiredService<AppCallbacks>().SignInFailed);
                    context.HandleResponse();

                    return Task.CompletedTask;
                };
            });
    }

    public static IEndpointRouteBuilder MapGoogleSignInEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var google = endpoints.MapGroup("/api/auth/google")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";

                return await next(context);
            });

        google.MapGet("/start", Start)
            .WithName("StartGoogleSignIn")
            .AllowAnonymous();

        google.MapGet("/complete", CompleteAsync)
            .WithName("CompleteGoogleSignIn")
            .AllowAnonymous();

        return endpoints;
    }

    public static Results<ChallengeHttpResult, ValidationProblem> Start(
        [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod,
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        AppCallbacks callbacks)
    {
        var errors = new Dictionary<string, string[]>();

        if (!callbacks.IsAllowed(redirectUri))
        {
            errors["redirect_uri"] = ["The redirect URI is not allowed."];
        }

        if (!Pkce.IsValidS256Challenge(codeChallenge))
        {
            errors["code_challenge"] = ["A PKCE S256 code challenge is required."];
        }

        if (codeChallengeMethod is not null && codeChallengeMethod != Pkce.S256)
        {
            errors["code_challenge_method"] = ["Only S256 is supported."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var properties = new AuthenticationProperties { RedirectUri = CompletePath };
        properties.Items[CodeChallengeItem] = codeChallenge;

        return TypedResults.Challenge(properties, [GoogleDefaults.AuthenticationScheme]);
    }

    public static async Task<IResult> CompleteAsync(
        HttpContext httpContext,
        ExternalSignInCompletion completion,
        AppCallbacks callbacks,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(GoogleSignIn));
        var result = await httpContext.AuthenticateAsync(ExternalScheme);
        await httpContext.SignOutAsync(ExternalScheme);

        if (!result.Succeeded)
        {
            return Results.Redirect(callbacks.SignInFailed);
        }

        // Only from the protected state written at /start.
        var codeChallenge = result.Properties?.GetString(CodeChallengeItem);

        if (!Pkce.IsValidS256Challenge(codeChallenge))
        {
            logger.LogWarning("Google sign-in completed without a valid protected PKCE challenge.");

            return Results.Problem(title: "Invalid sign-in state.", statusCode: StatusCodes.Status400BadRequest);
        }

        var subject = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("Google sign-in completed without a subject.");

            return Results.Redirect(callbacks.SignInFailed);
        }

        var callback = await completion.CompleteAsync(
            new VerifiedExternalIdentity(
                Provider,
                subject,
                result.Principal.FindFirstValue(ClaimTypes.Email),
                string.Equals(result.Principal.FindFirstValue(EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase),
                result.Principal.FindFirstValue(ClaimTypes.Name)),
            codeChallenge!,
            cancellationToken);

        return Results.Redirect(callback);
    }
}
