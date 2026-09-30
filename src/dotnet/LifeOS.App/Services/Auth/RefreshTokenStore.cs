namespace LifeOS.App.Services.Auth;

// The LifeOS refresh token in platform secure storage (Android Keystore-backed). The access token
// is never stored; it lives in memory only (TokenSession).
public sealed class RefreshTokenStore
{
	private const string Key = "lifeos.refresh_token";

	// A value that cannot be read (e.g. after a device backup restore without the Keystore key) is
	// removed and treated as signed out.
	public async Task<string?> GetAsync()
	{
		try
		{
			return await SecureStorage.Default.GetAsync(Key);
		}
		catch (Exception)
		{
			Clear();

			return null;
		}
	}

	public async Task<bool> TrySaveAsync(string refreshToken)
	{
		try
		{
			await SecureStorage.Default.SetAsync(Key, refreshToken);

			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public void Clear()
	{
		try
		{
			SecureStorage.Default.Remove(Key);
		}
		catch (Exception)
		{
			// Nothing more to do: an unreadable entry is ignored by GetAsync anyway.
		}
	}
}
