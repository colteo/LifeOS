namespace LifeOS.Contracts.Auth;

// ExpiresIn is the access-token lifetime in seconds.
public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn);
