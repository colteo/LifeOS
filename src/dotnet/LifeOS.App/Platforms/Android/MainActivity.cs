using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using LifeOS.App.PushNotifications;
using LifeOS.App.Services.Notifications;

namespace LifeOS.App;

// SingleTop: tapping a notification while LifeOS runs delivers the intent to OnNewIntent instead of
// starting a second activity (AUTO-001 §11 tap routing).
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	// Launched from a notification while LifeOS was closed.
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		NotificationIntents.Consume(Intent, PendingNavigation());
	}

	// Tapped while LifeOS was running (foreground or background).
	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);
		NotificationIntents.Consume(intent, PendingNavigation());
	}

	private static PendingNotificationNavigation? PendingNavigation() =>
		IPlatformApplication.Current?.Services.GetService<PendingNotificationNavigation>();
}
