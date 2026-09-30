using Android.App;
using Android.Content;
using Android.Content.PM;

namespace LifeOS.App;

// Receives the Google sign-in result (lifeos://auth?code=… or ?error=…) from the system browser and
// hands it back to WebAuthenticator.
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
	[Intent.ActionView],
	Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
	DataScheme = "lifeos",
	DataHost = "auth")]
public class WebAuthenticatorCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
