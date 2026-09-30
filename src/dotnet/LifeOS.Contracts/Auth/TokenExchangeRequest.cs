namespace LifeOS.Contracts.Auth;

// Exchanges the one-time authorization code from an external sign-in, with its PKCE verifier.
public sealed record TokenExchangeRequest(string? Code, string? CodeVerifier);
