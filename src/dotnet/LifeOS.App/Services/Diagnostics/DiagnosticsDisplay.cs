using LifeOS.App.Services.Notifications;

namespace LifeOS.App.Services.Diagnostics;

// Text for the Diagnostics page. Never shows tokens, headers or raw API responses.
public static class DiagnosticsDisplay
{
	public const string GenericError = "Could not send the test notification. Check the connection and try again.";

#if DEBUG
	public static bool IsRelease => false;
#else
	public static bool IsRelease => true;
#endif

	public static string Environment(bool isRelease) => isRelease ? "Production" : "Development";

	public static string VersionLabel(string version) => $"LifeOS {version}";

	// The host only: no scheme, path or port details beyond what identifies the API.
	public static string ApiHost(Uri baseAddress) => baseAddress.IsDefaultPort ? baseAddress.Host : $"{baseAddress.Host}:{baseAddress.Port}";

	public static string TestNotificationMessage(TestNotificationResult result) => result.Status switch
	{
		TestNotificationStatus.Sent => $"Sent to {result.Devices} device(s). Sent: {result.Sent}. Failed: {result.Failed}.",
		TestNotificationStatus.NoActiveDevice => "No active notification device.",
		TestNotificationStatus.PushDisabled => "Push notifications are not configured.",
		TestNotificationStatus.RateLimited => "Try again later.",
		_ => GenericError
	};
}
