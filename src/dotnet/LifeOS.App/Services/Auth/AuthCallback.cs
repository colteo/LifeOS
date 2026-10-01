namespace LifeOS.App.Services.Auth;

// The URI the API redirects to after Google sign-in, per build: the Debug app (it.colazzo.lifeos.dev)
// and the Release app (it.colazzo.lifeos) use different schemes, so both can be installed without
// Android asking which one should open the result. Each API environment redirects only to its own
// app's scheme (Development: lifeos-dev, otherwise lifeos).
public static class AuthCallback
{
#if DEBUG
	public const string Scheme = "lifeos-dev";
#else
	public const string Scheme = "lifeos";
#endif

	public const string Host = "auth";

	public const string Uri = Scheme + "://" + Host;
}
