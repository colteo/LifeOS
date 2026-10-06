using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LifeOS.UnitTests.Fakes;

// A synthetic Google service-account key, generated per test run (fresh RSA key, example.com-style
// ids). It parses like a real key but authorizes nothing; tests never contact Google.
internal static class TestServiceAccount
{
    public const string ProjectId = "lifeos-test-project";
    public const string ClientEmail = "fcm-sender@lifeos-test-project.iam.gserviceaccount.com";

    private static readonly Lazy<string> PrivateKeyPem = new(() =>
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    });

    public static string PrivateKey => PrivateKeyPem.Value;

    public static string Json(string type = "service_account") => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = type,
        ["project_id"] = ProjectId,
        ["private_key_id"] = "0000000000000000000000000000000000000000",
        ["private_key"] = PrivateKey,
        ["client_email"] = ClientEmail,
        ["client_id"] = "000000000000000000000",
        ["token_uri"] = "https://oauth2.googleapis.com/token"
    });

    // An "authorized_user" credential: valid JSON, but not a service account.
    public static string AuthorizedUserJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "authorized_user",
        ["client_id"] = "example.apps.googleusercontent.com",
        ["client_secret"] = "not-a-secret",
        ["refresh_token"] = "not-a-token"
    });

    public static string Base64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
}
