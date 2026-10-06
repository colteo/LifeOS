using Android.App;
using Android.Runtime;
using LifeOS.App.PushNotifications;

namespace LifeOS.App;

#if DEBUG
// Debug builds allow cleartext HTTP to the development API on the emulator host (10.0.2.2).
[Application(NetworkSecurityConfig = "@xml/network_security_config")]
#else
[Application]
#endif
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	// AUTO-001: the notification channel exists before any notification arrives.
	public override void OnCreate()
	{
		base.OnCreate();
		AndroidPushPlatform.EnsureChannel(this);
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
