namespace LifeOS.Contracts.Auth;

// Development-only sign-in. The provider is fixed by the server and never part of the request.
public sealed record DevSignInRequest(string? Subject, string? Email, string? DisplayName);
