using System.Net;

namespace LifeOS.App.Services;

// The LifeOS API addresses the app may use. Plain .NET (no MAUI), unit-tested.
public static class ApiBaseUrls
{
	// Development API on the PC: the physical phone reaches it through `adb reverse tcp:5050 tcp:5050`.
	public static readonly Uri DevelopmentLocalhost = new("http://localhost:5050/");

	// Development API seen from the Android emulator (the host machine).
	public static readonly Uri DevelopmentEmulatorHost = new("http://10.0.2.2:5050/");

	// Development: (API calls, Google sign-in in the system browser). The browser always uses localhost:
	// the development Google client is registered with http://localhost:5050/signin-google, and the API
	// scopes its sign-in cookies to that host. On the emulator that needs `adb -e reverse tcp:5050 tcp:5050`.
	public static (Uri BaseAddress, Uri BrowserBaseAddress) Development(bool isAndroidEmulator) =>
		isAndroidEmulator
			? (DevelopmentEmulatorHost, DevelopmentLocalhost)
			: (DevelopmentLocalhost, DevelopmentLocalhost);

	// Release: the build-time LifeOSApiBaseUrl. The build already rejects invalid values; this is the
	// same rule at startup, so a Release app can never talk to a development or plain-HTTP API.
	// The result always ends with "/" so relative paths ("api/...") resolve under it.
	public static bool TryParseProduction(string? value, out Uri? baseAddress, out string? error)
	{
		baseAddress = null;

		if (string.IsNullOrWhiteSpace(value))
		{
			error = "No production API URL was set at build time (LifeOSApiBaseUrl).";
			return false;
		}

		if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
		{
			error = "The production API URL is not an absolute URL.";
			return false;
		}

		if (uri.Scheme != Uri.UriSchemeHttps)
		{
			error = "The production API URL must use HTTPS.";
			return false;
		}

		if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
		{
			error = "The production API URL must not contain credentials, a query or a fragment.";
			return false;
		}

		if (IsLocalOrEmulator(uri))
		{
			error = "The production API URL must not point at a local or emulator address.";
			return false;
		}

		var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
		baseAddress = new UriBuilder(uri) { Path = path }.Uri;
		error = null;

		return true;
	}

	private static bool IsLocalOrEmulator(Uri uri) =>
		uri.IsLoopback
		|| uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
		|| (IPAddress.TryParse(uri.Host, out var address)
			&& (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Parse("10.0.2.2"))));
}
