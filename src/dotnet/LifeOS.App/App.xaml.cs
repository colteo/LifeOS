using LifeOS.App.Services.Auth;
using LifeOS.App.Services.Users;

namespace LifeOS.App;

public partial class App : Application
{
	private readonly AuthService _auth;
	private readonly TimeZoneSynchronizer _timeZone;

	public App(AuthService auth, TimeZoneSynchronizer timeZone)
	{
		InitializeComponent();

		_auth = auth;
		_timeZone = timeZone;

		// AUTO-001: after session restore or sign-in has loaded the profile (state Authenticated).
		_auth.StateChanged += SynchronizeTimeZone;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "LifeOS" };

		// AUTO-001: the device zone may have changed while the app was in the background.
		window.Resumed += (_, _) => SynchronizeTimeZone();

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
}
