using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using LifeOS.App.Services.Auth;
using LifeOS.App.Services.Notifications;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Notifications;

namespace LifeOS.UnitTests.App;

// AUTO-003A app: the Notifications section of Settings, its API client on the authenticated pipeline,
// the form rules, and the Finance reminder tap routes. Razor is checked by source, as in
// WeeklyReviewAppTests.
public class FinanceReminderAppTests
{
    // ---- Settings page ----

    [Fact]
    public void Settings_ShowsBothReminderToggles_AndQuietHours()
    {
        var settings = Settings();

        Assert.Contains("<h2 class=\"lo-section-title\">Notifications</h2>", settings);
        Assert.Contains("Recurring transaction reminders", settings);
        Assert.Contains("Planned expense reminders", settings);
        Assert.Contains("Quiet hours start", settings);
        Assert.Contains("Quiet hours end", settings);
        Assert.Equal(2, Count(settings, "role=\"switch\""));
        Assert.Equal(2, Count(settings, "type=\"time\""));
        Assert.Contains("@bind=\"preferences.RecurringTransactionReminders\"", settings);
        Assert.Contains("@bind=\"preferences.PlannedExpenseReminders\"", settings);
    }

    [Fact]
    public void Settings_LoadsAndSavesThroughTheApiClient_WithLoadingErrorAndSavedStates()
    {
        var settings = Settings();

        Assert.Contains("@inject NotificationPreferencesApiClient PreferencesApi", settings);
        Assert.Contains("PreferencesApi.GetAsync()", settings);
        Assert.Contains("PreferencesApi.SaveAsync(preferences.ToRequest())", settings);
        Assert.Contains("Loading notification settings...", settings);
        Assert.Contains("Retry", settings);
        Assert.Contains("Notification settings saved.", settings);
        Assert.Contains("Save notification settings", settings);

        // The existing Settings content is kept: account details, sign out, Diagnostics link.
        Assert.Contains("Sign out", settings);
        Assert.Contains("href=\"diagnostics\"", settings);
    }

    [Fact]
    public void Client_IsRegisteredOnTheAuthorizedPipeline()
    {
        var program = File.ReadAllText(Path.Combine(AppRoot(), "MauiProgram.cs"));

        Assert.Contains("new NotificationPreferencesApiClient(CreateAuthorizedHttpClient(services))", program);
    }

    // ---- API client ----

    [Fact]
    public async Task Client_SendsTheAccessToken_AndReadsThePreferences()
    {
        var session = await SessionAsync();
        var requests = new List<(string Method, string Path, string? Token)>();
        var client = Client(session, request =>
        {
            requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new NotificationPreferencesResponse(true, false, "22:00", "08:00"))
            };
        });

        var result = await client.GetAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(new NotificationPreferencesResponse(true, false, "22:00", "08:00"), result.Value);
        Assert.Equal([("GET", "/api/notification-preferences", (string?)"access-a")], requests);
    }

    [Fact]
    public async Task Client_PutsTheWholeForm()
    {
        var session = await SessionAsync();
        SetNotificationPreferencesRequest? sent = null;
        var client = Client(session, request =>
        {
            Assert.Equal(("PUT", "access-a"), (request.Method.Method, request.Headers.Authorization?.Parameter));
            sent = request.Content!.ReadFromJsonAsync<SetNotificationPreferencesRequest>().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var result = await client.SaveAsync(new SetNotificationPreferencesRequest(false, true, "23:00", "07:30"));

        Assert.True(result.IsSuccess);
        Assert.Equal(new SetNotificationPreferencesRequest(false, true, "23:00", "07:30"), sent);
    }

    [Fact]
    public async Task Client_ReportsTheApisValidationMessage()
    {
        var session = await SessionAsync();
        var client = Client(session, _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new
            {
                title = "One or more validation errors occurred.",
                status = 400,
                errors = new Dictionary<string, string[]> { ["quietHoursEnd"] = ["Quiet hours must start and end at different times."] }
            }, options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        });

        var result = await client.SaveAsync(new SetNotificationPreferencesRequest(true, true, "22:00", "22:00"));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("different times", StringComparison.Ordinal));
    }

    // ---- Form ----

    [Fact]
    public void Form_ValidatesQuietHours_AndNormalizesTimes()
    {
        var form = NotificationPreferencesForm.From(new NotificationPreferencesResponse(true, true, "22:00", "08:00"));

        Assert.Null(form.Validate());

        form.QuietHoursEnd = "22:00:00";
        Assert.Equal("Quiet hours must start and end at different times.", form.Validate());

        form.QuietHoursEnd = "";
        Assert.Equal("Enter a start and an end time for quiet hours.", form.Validate());

        form.QuietHoursEnd = "07:45:00";
        form.PlannedExpenseReminders = false;
        Assert.Null(form.Validate());
        Assert.Equal(new SetNotificationPreferencesRequest(true, false, "22:00", "07:45"), form.ToRequest());
    }

    // ---- Notification tap ----

    [Theory]
    [InlineData("finance_recurring", NotificationTargetKind.FinanceRecurring)]
    [InlineData("finance_planned_expense", NotificationTargetKind.FinancePlannedExpense)]
    public void FinanceReminderTap_OpensThePlannedTabOfTransactions(string type, NotificationTargetKind kind)
    {
        var id = Guid.CreateVersion7();

        var target = NotificationTap.Parse(type, id.ToString("D"));

        Assert.Equal(new NotificationTarget(kind, id), target);
        Assert.Equal("finance/transactions?tab=planned", NotificationTap.PathFor(target!));
        Assert.Equal("finance/transactions?tab=planned", NotificationTap.PathFor(NotificationTap.Parse(type, "not-a-guid")!));
    }

    [Fact]
    public void FinanceReminderTap_TargetsAnExistingRouteAndTab()
    {
        var transactions = File.ReadAllText(Path.Combine(ComponentsRoot(), "Pages", "Finance", "Transactions.razor"));

        Assert.StartsWith("@page \"/finance/transactions\"", transactions);
        Assert.Equal(LifeOS.App.Services.Finance.TransactionsTab.Planned, LifeOS.App.Services.Finance.TransactionsTabs.Parse("planned"));
        Assert.Contains("[SupplyParameterFromQuery(Name = TransactionsTabs.QueryName)]", transactions);
    }

    [Fact]
    public void ExistingTapsAreUnchanged()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal($"weekly-reviews/{id:D}", NotificationTap.PathFor(NotificationTap.Parse("weekly_review", id.ToString())!));
        Assert.Null(NotificationTap.PathFor(NotificationTap.Parse("test", null)!));
        Assert.Null(NotificationTap.Parse("finance", id.ToString()));
    }

    // ---- Helpers ----

    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    private static async Task<TokenSession> SessionAsync()
    {
        var session = new TokenSession(
            new AuthApiClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))) { BaseAddress = new("http://test/") }),
            new RefreshTokenStore());
        await session.EstablishAsync(new TokenResponse("access-a", "synthetic-refresh", 3600));
        return session;
    }

    private static NotificationPreferencesApiClient Client(TokenSession session, Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new HttpClient(new AuthorizationMessageHandler(session, new StubHandler(send))) { BaseAddress = new("http://test/") });

    private static string Settings() => File.ReadAllText(Path.Combine(ComponentsRoot(), "Pages", "Settings.razor"));

    private static string AppRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App"));

    private static string ComponentsRoot() => Path.Combine(AppRoot(), "Components");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
