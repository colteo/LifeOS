namespace LifeOS.App.Services.Auth;

public enum AuthCallStatus
{
	Success,

	// The server answered and refused (e.g. invalid or revoked token): credentials are not valid.
	Rejected,

	// No usable answer (network error, timeout, server error): credentials may still be valid.
	Unavailable
}

// Keeps "the server rejected us" apart from "the server could not be reached", so a temporary
// connectivity problem never destroys valid credentials.
public sealed record AuthCallResult<T>(AuthCallStatus Status, T? Value)
{
	public static AuthCallResult<T> Success(T value) => new(AuthCallStatus.Success, value);

	public static AuthCallResult<T> Rejected() => new(AuthCallStatus.Rejected, default);

	public static AuthCallResult<T> Unavailable() => new(AuthCallStatus.Unavailable, default);
}
