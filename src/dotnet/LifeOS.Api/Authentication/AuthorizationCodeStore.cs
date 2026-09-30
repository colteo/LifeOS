using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LifeOS.Api.Authentication;

public sealed record AuthorizationCodeGrant(Guid UserId, string CodeChallenge, string RedirectUri);

// Short-lived, single-use LifeOS authorization codes, handed to the app through its callback URI and
// exchanged (with the PKCE verifier) for a LifeOS session.
//
// In memory, single API instance. Entries are keyed by SHA-256 of the code, so raw codes are not
// kept. TryRemove makes consumption atomic: of two concurrent exchanges only one gets the grant.
// A distributed store is needed only when the API runs as several instances.
public sealed class AuthorizationCodeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private const int CodeByteLength = 32;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public AuthorizationCodeStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public string Create(Guid userId, string codeChallenge, string redirectUri)
    {
        var now = _timeProvider.GetUtcNow();
        RemoveExpired(now);

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(CodeByteLength));
        _entries[Hash(code)] = new Entry(new AuthorizationCodeGrant(userId, codeChallenge, redirectUri), now + Lifetime);

        return code;
    }

    // Removes the code whatever the outcome; returns the grant only if it had not expired.
    public AuthorizationCodeGrant? TryConsume(string code)
    {
        if (!_entries.TryRemove(Hash(code), out var entry))
        {
            return null;
        }

        return _timeProvider.GetUtcNow() < entry.ExpiresAtUtc ? entry.Grant : null;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAtUtc <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private sealed record Entry(AuthorizationCodeGrant Grant, DateTimeOffset ExpiresAtUtc);
}
