using LifeOS.Contracts.Auth;

namespace LifeOS.App.Services.Auth;

public enum SessionUpdate
{
	// A valid access token is now in memory.
	Established,

	// No valid session: nothing stored, or the server rejected the refresh token. Local state cleared.
	Ended,

	// The server could not be reached. The stored refresh token is kept for a later retry.
	Unavailable,

	// The rotated refresh token could not be stored. Local state cleared (the old token is revoked).
	StorageFailed
}

// Owns the LifeOS tokens on the device: the access token in memory, the refresh token in secure
// storage.
//
// Refresh is single-flight: the server rotates refresh tokens without a grace window, so two
// concurrent refreshes with the same token would end the session. Only one refresh runs at a time;
// callers that waited reuse the token it produced.
public sealed class TokenSession
{
	private readonly AuthApiClient _authApi;
	private readonly RefreshTokenStore _store;
	private readonly SemaphoreSlim _refreshLock = new(1, 1);
	private volatile string? _accessToken;

	public TokenSession(AuthApiClient authApi, RefreshTokenStore store)
	{
		_authApi = authApi;
		_store = store;
	}

	// Raised when the session is ended because the server rejected it or storage failed.
	public event Action<string?>? SessionEnded;

	public string? AccessToken => _accessToken;

	// Stores a new token pair: the refresh token is persisted BEFORE the session counts as established.
	public async Task<SessionUpdate> EstablishAsync(TokenResponse tokens)
	{
		await _refreshLock.WaitAsync();

		try
		{
			return await StoreAsync(tokens);
		}
		finally
		{
			_refreshLock.Release();
		}
	}

	// Refreshes the access token. failedAccessToken is the token a request was sent with (null when
	// restoring at startup): if the current token already differs, another caller has refreshed and
	// no new request is made.
	public async Task<SessionUpdate> RefreshAsync(string? failedAccessToken, CancellationToken cancellationToken = default)
	{
		await _refreshLock.WaitAsync(cancellationToken);

		try
		{
			var current = _accessToken;

			if (current is not null && !string.Equals(current, failedAccessToken, StringComparison.Ordinal))
			{
				return SessionUpdate.Established;
			}

			var refreshToken = await _store.GetAsync();

			if (refreshToken is null)
			{
				_accessToken = null;

				return SessionUpdate.Ended;
			}

			var result = await _authApi.RefreshAsync(refreshToken, cancellationToken);

			switch (result.Status)
			{
				case AuthCallStatus.Success:
					return await StoreAsync(result.Value!);

				case AuthCallStatus.Rejected:
					ClearLocal();
					SessionEnded?.Invoke("Your session has expired. Please sign in again.");

					return SessionUpdate.Ended;

				default:
					// Keep the stored refresh token: the server may simply be unreachable right now.
					return SessionUpdate.Unavailable;
			}
		}
		finally
		{
			_refreshLock.Release();
		}
	}

	// Ends the session locally, e.g. when the server keeps rejecting a freshly refreshed token.
	public async Task EndAsync(string? reason)
	{
		await _refreshLock.WaitAsync();

		try
		{
			ClearLocal();
		}
		finally
		{
			_refreshLock.Release();
		}

		SessionEnded?.Invoke(reason);
	}

	// Local sign-out: returns the stored refresh token (for a best-effort server logout) and clears
	// everything on the device.
	public async Task<string?> ClearAsync()
	{
		await _refreshLock.WaitAsync();

		try
		{
			var refreshToken = await _store.GetAsync();
			ClearLocal();

			return refreshToken;
		}
		finally
		{
			_refreshLock.Release();
		}
	}

	private async Task<SessionUpdate> StoreAsync(TokenResponse tokens)
	{
		if (!await _store.TrySaveAsync(tokens.RefreshToken))
		{
			// Do not continue with the previous refresh token: after rotation it is revoked.
			ClearLocal();
			await _authApi.LogoutAsync(tokens.RefreshToken);
			SessionEnded?.Invoke("Unable to save the session on this device. Please sign in again.");

			return SessionUpdate.StorageFailed;
		}

		_accessToken = tokens.AccessToken;

		return SessionUpdate.Established;
	}

	private void ClearLocal()
	{
		_accessToken = null;
		_store.Clear();
	}
}
