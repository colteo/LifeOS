namespace LifeOS.App.Services.Users;

// The device's current IANA time zone id (AUTO-001 §6). Never a UTC offset, never a Windows id;
// the server validates whatever is sent.
public static class DeviceTimeZone
{
	// .NET caches TimeZoneInfo.Local; the device zone may have changed while the process lived.
	public static string? Current(Func<string?> platformZoneId)
	{
		TimeZoneInfo.ClearCachedData();

		return Resolve(TimeZoneInfo.Local, platformZoneId);
	}

	// Prefers the .NET local zone when it is an IANA zone; otherwise the platform's own id
	// (Android: java.util.TimeZone.getDefault().getID()).
	public static string? Resolve(TimeZoneInfo local, Func<string?> platformZoneId)
	{
		if (local.HasIanaId && !string.IsNullOrWhiteSpace(local.Id))
		{
			return local.Id;
		}

		var platform = platformZoneId()?.Trim();

		return string.IsNullOrEmpty(platform) ? null : platform;
	}
}
