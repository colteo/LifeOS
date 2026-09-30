namespace LifeOS.Api.Authentication;

// Guard for the Development-only sign-in endpoint (ADR-006). It is mapped only when the
// environment is Development AND the flag is explicitly enabled. Enabling the flag in any
// other environment fails startup.
public static class DevelopmentSignIn
{
    public const string EnabledKey = "Authentication:DevelopmentSignIn:Enabled";

    // The development endpoint only ever creates identities for this provider.
    public const string Provider = "dev";

    public static bool IsEnabled(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>(EnabledKey))
        {
            return false;
        }

        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{EnabledKey} is enabled in the '{builder.Environment.EnvironmentName}' environment. "
                + "Development sign-in is allowed only in Development.");
        }

        return true;
    }
}
