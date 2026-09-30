using Microsoft.IdentityModel.JsonWebTokens;

namespace LifeOS.Api.Authentication;

// The LifeOS user of the current request, bound from the validated access token's subject.
// Use only on endpoints that require authorization. Handlers receive UserId explicitly.
public sealed record AuthenticatedUser(Guid UserId)
{
    public static ValueTask<AuthenticatedUser?> BindAsync(HttpContext context)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        return ValueTask.FromResult(
            Guid.TryParse(subject, out var userId) && userId != Guid.Empty
                ? new AuthenticatedUser(userId)
                : null);
    }
}
