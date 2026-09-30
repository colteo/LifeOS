using System.Security.Cryptography;
using System.Text;

namespace LifeOS.Application.Authentication;

// Opaque refresh tokens: 32 cryptographically random bytes, base64url-encoded.
// Only the SHA-256 hash is ever stored; the raw token is returned to the caller once.
public static class RefreshTokens
{
    private const int TokenByteLength = 32;

    public static string Generate() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));

    // Lowercase hex SHA-256 of the token's UTF-8 bytes (64 characters).
    public static string Hash(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
