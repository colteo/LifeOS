using LifeOS.Application.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace LifeOS.Api.Authentication;

public static class AuthenticationSetup
{
    // JWT bearer validation of LifeOS access tokens, plus token issuing.
    // No global fallback policy yet: each endpoint declares its own authorization.
    public static IServiceCollection AddLifeOSAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = LifeOSTokenOptions.FromConfiguration(configuration);

        services.AddSingleton(options);
        services.AddSingleton(new UserSessionOptions(options.RefreshTokenLifetime));
        services.AddSingleton<AccessTokenIssuer>();

        services
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

        services.AddAuthorization();

        return services;
    }
}
