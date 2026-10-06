using LifeOS.Contracts.Devices;

namespace LifeOS.App.Services.Notifications;

// The platform side of push: OS permission and the FCM token (Android: Firebase Messaging).
// Implementations never throw; "unavailable" is null/false.
public interface IPushPlatform
{
	// Shows the OS notification prompt if the OS still allows it (Android 13+); otherwise nothing.
	Task RequestPermissionAsync();

	// The notifications switch for this app (permission and the user's system setting).
	bool AreNotificationsEnabled();

	// Null when push is unavailable here (no Firebase configuration, no Google Play services, offline).
	Task<string?> GetTokenAsync();

	Task DeleteTokenAsync();
}

// Platforms without push.
public sealed class NoPushPlatform : IPushPlatform
{
	public Task RequestPermissionAsync() => Task.CompletedTask;

	public bool AreNotificationsEnabled() => false;

	public Task<string?> GetTokenAsync() => Task.FromResult<string?>(null);

	public Task DeleteTokenAsync() => Task.CompletedTask;
}

// AUTO-001 §12 device lifecycle of this installation, for the signed-in user:
// - after sign-in / session restore and on every resume: register (permission + current token), which
//   also refreshes last_seen_at_utc; a revoked permission registers notificationsPermitted=false;
// - on a new FCM token: register again (the App triggers it only while signed in);
// - before sign-out: best-effort DELETE, then delete the local FCM token, both bounded in time.
// Best effort everywhere: never throws, never blocks sign-in, Home or sign-out, never shows UI.
// Delegates allow testing without MAUI.
public sealed class DeviceRegistrar
{
	public const string PermissionRequestedKey = "lifeos.push.permission-requested";
	public const string Platform = "Android";

	private readonly IPushPlatform _platform;
	private readonly Func<string> _installationId;
	private readonly Func<bool> _permissionRequested;
	private readonly Action _markPermissionRequested;
	private readonly Func<string, RegisterDeviceRequest, CancellationToken, Task<bool>> _register;
	private readonly Func<string, CancellationToken, Task<bool>> _unregister;
	private readonly Func<Guid, bool> _isCurrentUser;
	private readonly SemaphoreSlim _lock = new(1, 1);

	public DeviceRegistrar(
		IPushPlatform platform,
		Func<string> installationId,
		Func<bool> permissionRequested,
		Action markPermissionRequested,
		Func<string, RegisterDeviceRequest, CancellationToken, Task<bool>> register,
		Func<string, CancellationToken, Task<bool>> unregister,
		Func<Guid, bool> isCurrentUser)
	{
		_platform = platform;
		_installationId = installationId;
		_permissionRequested = permissionRequested;
		_markPermissionRequested = markPermissionRequested;
		_register = register;
		_unregister = unregister;
		_isCurrentUser = isCurrentUser;
	}

	public async Task RegisterAsync(Guid userId)
	{
		var entered = false;

		try
		{
			// One registration at a time; a queued one re-reads permission and token.
			await _lock.WaitAsync();
			entered = true;

			if (!_isCurrentUser(userId))
			{
				return;
			}

			// The prompt is shown once per installation, after the first sign-in (AUTO-001 §12).
			if (!_permissionRequested())
			{
				_markPermissionRequested();
				await _platform.RequestPermissionAsync();
			}

			var permitted = _platform.AreNotificationsEnabled();
			var token = permitted ? await _platform.GetTokenAsync() : null;

			// Permitted but no token (push unavailable on this device right now): nothing to report.
			if (permitted && string.IsNullOrEmpty(token))
			{
				return;
			}

			// The account may have changed while the permission prompt or the token was pending.
			if (!_isCurrentUser(userId))
			{
				return;
			}

			await _register(_installationId(), Request(permitted, token), CancellationToken.None);
		}
		catch (Exception)
		{
			// Best effort: the next sign-in, resume or token change tries again.
		}
		finally
		{
			if (entered)
			{
				_lock.Release();
			}
		}
	}

	// Called before the LifeOS session is cleared (the DELETE needs it). Waits at most `timeout` in
	// total; sign-out continues whatever happens here.
	public async Task UnregisterAsync(TimeSpan timeout)
	{
		using var deadline = new CancellationTokenSource(timeout);
		var entered = false;

		try
		{
			// Let an in-flight registration finish first, so it cannot re-activate the row afterwards;
			// at most half the budget, so the DELETE still gets the rest.
			entered = await _lock.WaitAsync(timeout / 2);

			await _unregister(_installationId(), deadline.Token);
			await _platform.DeleteTokenAsync().WaitAsync(deadline.Token);
		}
		catch (Exception)
		{
			// Best effort: an offline sign-out leaves the row Active until the next sign-in on this
			// installation re-owns it, or FCM reports the deleted token as unregistered.
		}
		finally
		{
			if (entered)
			{
				_lock.Release();
			}
		}
	}

	// notificationsPermitted=false never carries a token (the server clears it: PermissionDenied).
	public static RegisterDeviceRequest Request(bool permitted, string? token) =>
		permitted ? new RegisterDeviceRequest(Platform, token, true) : new RegisterDeviceRequest(Platform, null, false);
}
