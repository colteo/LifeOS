using LifeOS.Application.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace LifeOS.Api.Authentication;

public static class AuthenticationSetup
{
    // JWT bearer validation of LifeOS access tokens, plus token issuing.
    // Every endpoint requires an authenticated user unless it is explicitly marked AllowAnonymous
    // (fallback policy, ADR-006). Feature groups still declare RequireAuthorization explicitly.
    public static IServiceCollection AddLifeOSAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = LifeOSTokenOptions.FromConfiguration(configuration);

        services.AddSingleton(options);
        services.AddSingleton(new UserSessionOptions(options.RefreshTokenLifetime));
        services.AddSingleton<AccessTokenIssuer>();
        services.AddSingleton<AuthorizationCodeStore>();
        services.AddScoped<ExternalSignInCompletion>();

        var authentication = services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(options.SigningKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub"
                };
            });

        // JwtBearer stays the default scheme: the Google result never authenticates an API call.
        if (GoogleSignIn.IsEnabled(configuration))
        {
            authentication.AddLifeOSGoogle(configuration);
        }

        services.AddAuthorization(authorization =>
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }
}
