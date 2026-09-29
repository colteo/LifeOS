namespace LifeOS.App.Services;

public sealed class ApiSettings
{
	public ApiSettings(Uri baseAddress)
	{
		BaseAddress = baseAddress;
	}

	public Uri BaseAddress { get; }

	// Development only. On Android, the emulator reaches the host machine through 10.0.2.2;
	// a physical device reaches it through 127.0.0.1 after `adb reverse tcp:5050 tcp:5050`.
	public static ApiSettings ForDevelopment()
	{
		if (DeviceInfo.Platform != DevicePlatform.Android)
		{
			return new(new Uri("http://localhost:5050/"));
		}

		return DeviceInfo.DeviceType == DeviceType.Physical
			? new(new Uri("http://127.0.0.1:5050/"))
			: new(new Uri("http://10.0.2.2:5050/"));
	}
}
