using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LifeOS.Api.Authentication;

// PKCE (RFC 7636), S256 only: binds the one-time LifeOS authorization code to the app instance
// that started the sign-in.
public static class Pkce
{
    public const string S256 = "S256";

    private const int ChallengeByteLength = 32; // SHA-256
    private const int MinVerifierLength = 43;
    private const int MaxVerifierLength = 128;

    // RFC 7636: 43-128 characters of [A-Z] [a-z] [0-9] "-" "." "_" "~".
    public static bool IsValidVerifier(string? verifier) =>
        verifier is { Length: >= MinVerifierLength and <= MaxVerifierLength }
        && verifier.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');

    // A canonical, unpadded base64url encoding of exactly 32 bytes (a SHA-256 digest).
    public static bool IsValidS256Challenge(string? challenge)
    {
        if (challenge is null || !Base64Url.IsValid(challenge.AsSpan(), out var decodedLength) || decodedLength != ChallengeByteLength)
        {
            return false;
        }

        // Canonical: re-encoding the bytes gives back exactly the same text (no padding, no stray bits).
        return string.Equals(Base64Url.EncodeToString(Base64Url.DecodeFromChars(challenge)), challenge, StringComparison.Ordinal);
    }

    public static string ComputeS256Challenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    // Constant-time comparison of BASE64URL(SHA256(ASCII(verifier))) with the stored challenge.
    public static bool Matches(string verifier, string challenge) =>
        IsValidVerifier(verifier)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ComputeS256Challenge(verifier)),
            Encoding.ASCII.GetBytes(challenge));
}
