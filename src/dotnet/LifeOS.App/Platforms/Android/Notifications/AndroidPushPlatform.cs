using Android.App;
using Android.Content;
using Android.Gms.Extensions;
using AndroidX.Core.App;
using Firebase;
using Firebase.Messaging;
using LifeOS.App.Services.Notifications;

namespace LifeOS.App.PushNotifications;

// AUTO-001 §11/§12 on Android: the notification channel, the POST_NOTIFICATIONS permission and the FCM
// token. Without Firebase configuration (google-services.json) or Google Play services, push is simply
// unavailable: nothing here throws.
public sealed class AndroidPushPlatform : IPushPlatform
{
	// The single channel; the server sends android.notification.channel_id = "lifeos_general".
	public const string ChannelId = "lifeos_general";
	public const string ChannelName = "LifeOS";

	// Idempotent: creating an existing channel only updates its name.
	public static void EnsureChannel(Context context)
	{
		// Channels exist from Android 8 (API 26); earlier versions show notifications without one.
		if (!OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			return;
		}

		try
		{
			var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
			manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Default));
		}
		catch (Exception)
		{
			// Notifications are optional.
		}
	}

	public async Task RequestPermissionAsync()
	{
		try
		{
			await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.PostNotifications>);
		}
		catch (Exception)
		{
			// No foreground activity, or the OS refused to show the prompt: treated as not granted.
		}
	}

	public bool AreNotificationsEnabled()
	{
		try
		{
			return NotificationManagerCompat.From(global::Android.App.Application.Context)?.AreNotificationsEnabled() ?? false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public async Task<string?> GetTokenAsync()
	{
		try
		{
			if (!IsFirebaseConfigured())
			{
				return null;
			}

			// The binding marks these standard FCM calls "deprecated"; it exposes no other token API.
#pragma warning disable CS0618
			var token = await FirebaseMessaging.Instance.GetToken().AsAsync<Java.Lang.String>();
#pragma warning restore CS0618

			return token?.ToString();
		}
		catch (Exception)
		{
			return null;
		}
	}

	public async Task DeleteTokenAsync()
	{
		try
		{
			if (IsFirebaseConfigured())
			{
#pragma warning disable CS0618 // See GetTokenAsync.
				await FirebaseMessaging.Instance.DeleteToken().AsAsync();
#pragma warning restore CS0618
			}
		}
		catch (Exception)
		{
			// Best effort.
		}
	}

	// FirebaseApp initializes itself from google-services.json; without it there is no default app.
	private static bool IsFirebaseConfigured() =>
		FirebaseApp.GetApps(global::Android.App.Application.Context).Count > 0;
}
