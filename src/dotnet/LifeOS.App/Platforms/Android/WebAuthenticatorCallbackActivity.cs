using Android.App;
using Android.Content;
using Android.Content.PM;
using LifeOS.App.Services.Auth;

namespace LifeOS.App;

// Receives the Google sign-in result (<scheme>://auth?code=… or ?error=…) from the system browser and
// hands it back to WebAuthenticator. The scheme depends on the build (AuthCallback): lifeos-dev in
// Debug, lifeos in Release.
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
	[Intent.ActionView],
	Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
	DataScheme = AuthCallback.Scheme,
	DataHost = AuthCallback.Host)]
public class WebAuthenticatorCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
