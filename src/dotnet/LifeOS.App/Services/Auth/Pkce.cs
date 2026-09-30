using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LifeOS.App.Services.Auth;

// PKCE (RFC 7636, S256) for the Google sign-in. Plain .NET, no MAUI dependency.
public static class Pkce
{
	// 32 random bytes → 43 base64url characters, within RFC 7636's 43-128 unreserved characters.
	public static string CreateVerifier() =>
		Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

	// BASE64URL(SHA256(ASCII(verifier))).
	public static string CreateS256Challenge(string verifier) =>
		Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
