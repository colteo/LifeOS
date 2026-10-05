using LifeOS.App.Services.Auth;
using LifeOS.App.Services.Notifications;
using LifeOS.App.Services.Users;

namespace LifeOS.App;

public partial class App : Application
{
	private readonly AuthService _auth;
	private readonly TimeZoneSynchronizer _timeZone;
	private readonly DeviceRegistrar _devices;

	public App(AuthService auth, TimeZoneSynchronizer timeZone, DeviceRegistrar devices)
	{
		InitializeComponent();

		_auth = auth;
		_timeZone = timeZone;
		_devices = devices;

		// AUTO-001: after session restore or sign-in has loaded the profile (state Authenticated).
		_auth.StateChanged += SynchronizeTimeZone;
		_auth.StateChanged += RegisterDevice;

		// AUTO-001: a rotated FCM token is re-registered while signed in (ignored when signed out).
		PushTokenEvents.TokenChanged += RegisterDevice;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "LifeOS" };

		// AUTO-001: the device zone may have changed while the app was in the background.
		window.Resumed += (_, _) => SynchronizeTimeZone();

		// AUTO-001: permission may have been revoked and last_seen_at_utc is refreshed on resume.
		window.Resumed += (_, _) => RegisterDevice();

		return window;
	}

	// Fire and forget, off the UI thread: never delays navigation or shows sync UI.
	// The shared HTTP pipeline still handles rejected credentials. TimeZoneSynchronizer does not throw.
	private void SynchronizeTimeZone()
	{
		if (_auth.State == AuthState.Authenticated && _auth.CurrentUser is { } user)
		{
			_ = Task.Run(() => _timeZone.SynchronizeAsync(user.UserId));
		}
	}

	// Same rules as the time zone: background, best effort; DeviceRegistrar does not throw.
	private void RegisterDevice()
	{
		if (_auth.State == AuthState.Authenticated && _auth.CurrentUser is { } user)
		{
			_ = Task.Run(() => _devices.RegisterAsync(user.UserId));
		}
	}
}
