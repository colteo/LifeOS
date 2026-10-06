using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Firebase.Messaging;
using LifeOS.App.Services.Notifications;

namespace LifeOS.App.PushNotifications;

// AUTO-001 §11/§12. In the background, Android shows FCM notification messages itself (channel and tag
// from the message) and a tap launches MainActivity with the data as extras. In the foreground this
// service receives them and shows the same notification locally. A new token re-registers the
// installation while a LifeOS user is signed in.
[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class LifeOSFirebaseMessagingService : FirebaseMessagingService
{
	// The binding marks this standard FCM callback obsolete; it is still how token rotation arrives.
#pragma warning disable CS0672
	public override void OnNewToken(string token) => PushTokenEvents.RaiseTokenChanged();
#pragma warning restore CS0672

	public override void OnMessageReceived(RemoteMessage message)
	{
		try
		{
			var notification = message.GetNotification();

			if (notification is null)
			{
				return;
			}

			message.Data.TryGetValue("type", out var type);
			message.Data.TryGetValue("id", out var id);

			NotificationIntents.Show(this, notification.Tag, notification.Title, notification.Body, type, id);
		}
		catch (Exception)
		{
			// A notification that cannot be shown must never crash the app.
		}
	}
}

// Building the visible notification and reading a tapped one.
public static class NotificationIntents
{
	public const string TypeExtra = "type";
	public const string IdExtra = "id";

	// Same channel and tag as the server's message: a resend of one logical notification replaces it.
	public static void Show(Context context, string? tag, string? title, string? body, string? type, string? id)
	{
		AndroidPushPlatform.EnsureChannel(context);

		var tap = new Intent(context, typeof(MainActivity));
		tap.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
		tap.PutExtra(TypeExtra, type);
		tap.PutExtra(IdExtra, id);

		var pending = PendingIntent.GetActivity(
			context,
			(tag ?? string.Empty).GetHashCode(),
			tap,
			PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

		var built = new NotificationCompat.Builder(context, AndroidPushPlatform.ChannelId)
			.SetSmallIcon(Resource.Mipmap.appicon)!
			.SetContentTitle(title)!
			.SetContentText(body)!
			.SetPriority(NotificationCompat.PriorityDefault)!
			.SetAutoCancel(true)!
			.SetContentIntent(pending)!
			.Build();

		var manager = NotificationManagerCompat.From(context);

		if (built is not null && manager?.AreNotificationsEnabled() == true)
		{
			manager.Notify(tag, 0, built);
		}
	}

	// A launch or new intent from a notification tap: hand the target to the app once, then forget it
	// (so recreating the activity does not navigate again). Anything else is ignored.
	public static void Consume(Intent? intent, PendingNotificationNavigation? navigation)
	{
		try
		{
			if (intent?.Extras is not { } extras || navigation is null)
			{
				return;
			}

			var target = NotificationTap.Parse(extras.GetString(TypeExtra), extras.GetString(IdExtra));
			intent.RemoveExtra(TypeExtra);
			intent.RemoveExtra(IdExtra);

			if (target is not null)
			{
				navigation.Set(target);
			}
		}
		catch (Exception)
		{
			// Malformed launch data never crashes the app.
		}
	}
}
