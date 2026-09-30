namespace LifeOS.App.Services;

public sealed class ApiSettings
{
	public ApiSettings(Uri baseAddress, Uri browserBaseAddress)
	{
		BaseAddress = baseAddress;
		BrowserBaseAddress = browserBaseAddress;
	}

	// Used by the app's own API calls.
	public Uri BaseAddress { get; }

	// Used by the system browser for Google sign-in. It must be localhost: the API builds the Google
	// redirect URI (http://localhost:5050/signin-google, as registered with Google) and scopes its
	// sign-in cookies from this host.
	public Uri BrowserBaseAddress { get; }

	// Development only.
	// - Physical Android device: localhost through `adb reverse tcp:5050 tcp:5050`, for both.
	// - Android emulator: API calls through 10.0.2.2 (the host machine); Google sign-in through
	//   localhost, which needs `adb -e reverse tcp:5050 tcp:5050`.
	// - Other platforms: localhost.
	public static ApiSettings ForDevelopment()
	{
		var localhost = new Uri("http://localhost:5050/");

		return DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.DeviceType != DeviceType.Physical
			? new(new Uri("http://10.0.2.2:5050/"), localhost)
			: new(localhost, localhost);
	}
}
