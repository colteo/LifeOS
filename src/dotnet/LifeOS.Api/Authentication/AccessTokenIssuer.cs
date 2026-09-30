using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LifeOS.Api.Authentication;

// Issues short-lived LifeOS access tokens (HS256 JWT). The subject is the LifeOS UserId;
// no profile data is included.
public sealed class AccessTokenIssuer
{
    private readonly LifeOSTokenOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SigningCredentials _signingCredentials;
    private readonly JsonWebTokenHandler _tokenHandler = new();

    public AccessTokenIssuer(LifeOSTokenOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(options.SigningKey),
            SecurityAlgorithms.HmacSha256);
    }

    public TimeSpan Lifetime => _options.AccessTokenLifetime;

    public string Issue(Guid userId)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        return _tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + _options.AccessTokenLifetime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            },
            SigningCredentials = _signingCredentials
        });
    }
}
