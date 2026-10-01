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

	// Used by the system browser for Google sign-in: the API builds the Google redirect URI
	// (<host>/signin-google, as registered with Google) and scopes its sign-in cookies from this host.
	public Uri BrowserBaseAddress { get; }

	// Debug builds use the local development API; Release builds the production API URL fixed at
	// build time (LifeOSApiBaseUrl, validated by the build and again here).
	public static ApiSettings ForCurrentBuild()
	{
#if DEBUG
		return ForDevelopment();
#else
		return ForProduction(BuildSettings.ApiBaseUrl);
#endif
	}

	// Development only.
	// - Physical Android device: localhost through `adb reverse tcp:5050 tcp:5050`, for both.
	// - Android emulator: API calls through 10.0.2.2 (the host machine); Google sign-in through
	//   localhost, which needs `adb -e reverse tcp:5050 tcp:5050`.
	// - Other platforms: localhost.
	public static ApiSettings ForDevelopment()
	{
		var isAndroidEmulator = DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.DeviceType != DeviceType.Physical;
		var (baseAddress, browserBaseAddress) = ApiBaseUrls.Development(isAndroidEmulator);

		return new(baseAddress, browserBaseAddress);
	}

	// Production: one HTTPS address for both the API calls and the sign-in browser.
	public static ApiSettings ForProduction(string? apiBaseUrl) =>
		ApiBaseUrls.TryParseProduction(apiBaseUrl, out var baseAddress, out var error)
			? new(baseAddress!, baseAddress!)
			: throw new InvalidOperationException(error);
}
