using Google.Apis.Auth.OAuth2;

namespace LifeOS.Infrastructure.Notifications.Fcm;

// The FCM server configuration (AUTO-001 §11, PD-6). Both values are required together; the JSON
// is the decoded service-account key and is secret: never logged, never part of an error message.
public sealed record FcmOptions(string ProjectId, string ServiceAccountJson)
{
    public override string ToString() => $"FcmOptions {{ ProjectId = {ProjectId} }}";
}

// Loads the service-account credential with Google.Apis.Auth's typed loader, so only a
// service-account credential is accepted, scoped to FCM sending. The returned credential caches and
// refreshes its OAuth access token itself.
internal static class FcmCredentials
{
    public const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";

    public static ITokenAccess Load(string serviceAccountJson)
    {
        ServiceAccountCredential serviceAccount;

        try
        {
            serviceAccount = CredentialFactory.FromJson<ServiceAccountCredential>(serviceAccountJson);
        }
        catch (Exception)
        {
            // Deliberately no inner exception: parser messages can quote parts of the credential.
            throw new InvalidOperationException(
                "Notifications:Fcm:ServiceAccountJson is not a valid Google service-account key.");
        }

        return GoogleCredential.FromServiceAccountCredential(serviceAccount).CreateScoped(MessagingScope);
    }
}
