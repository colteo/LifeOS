using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LifeOS.App.Services;
using LifeOS.App.Services.Journal;
using LifeOS.Contracts.Journal;

namespace LifeOS.UnitTests.App;

// JRN-002 app: the Journal entry in Modules, the timeline/new/detail/edit pages, the API client, the
// entry draft (validation, local time → UTC, full-replacement payloads) and the display rules. Razor is
// checked by source, as in WeeklyReviewAppTests. The dock rules are in HomeNavigationTests.
public class JournalAppTests
{
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("PlusTwo", TimeSpan.FromHours(2), "PlusTwo", "PlusTwo");

    // ---- Navigation ----

    [Fact]
    public void Modules_ListsJournal_AndTheDockAndHeaderAreUnchanged()
    {
        var more = Source("Pages", "More.razor");

        Assert.Contains("new(\"Journal\", \"Your personal journal\", \"journal\", \"journal\")", more);
        Assert.Contains("case \"journal\":", Source("Shared", "AppIcon.razor"));
        Assert.DoesNotContain("journal", Source("Layout", "BottomDock.razor"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journal", Source("Layout", "AppHeader.razor"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pages_AreRouted_AndBackLinksFollowTheHierarchy()
    {
        Assert.StartsWith("@page \"/journal\"", List());
        Assert.StartsWith("@page \"/journal/new\"", New());
        Assert.StartsWith("@page \"/journal/{EntryId:guid}\"", Detail());
        Assert.StartsWith("@page \"/journal/{EntryId:guid}/edit\"", Edit());

        Assert.Contains("<PageHeader Title=\"@JournalDisplay.Title\" BackHref=\"more\" />", List());
        Assert.Contains("<PageHeader Title=\"New entry\" BackHref=\"journal\" />", New());
        Assert.Contains("<PageHeader Title=\"@JournalDisplay.Title\" BackHref=\"journal\" />", Detail());
        Assert.Contains("<PageHeader Title=\"Edit entry\" BackHref=\"@DetailHref\" />", Edit());
        Assert.Contains("$\"journal/{EntryId}\"", Edit());
        Assert.Equal("Journal", JournalDisplay.Title);
    }

    [Fact]
    public void Client_IsRegisteredOnTheAuthorizedPipeline()
    {
        var program = File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "MauiProgram.cs"));

        Assert.Contains("new LifeOS.App.Services.Journal.JournalApiClient(CreateAuthorizedHttpClient(services))", program);
    }

    // ---- Timeline page ----

    [Fact]
    public void List_HasLoadingErrorEmptyAndPagingStates()
    {
        var list = List();

        Assert.Contains("Loading journal...", list);
        Assert.Contains("Retry", list);
        Assert.Contains("No journal entries yet.", list);
        Assert.Contains("Load more", list);
        Assert.Contains("@if (nextCursor is not null)", list);
        Assert.Contains("<a href=\"journal/new\"", list);
    }

    [Fact]
    public void List_RendersEachEntry_InTheApiOrder()
    {
        var list = List();

        Assert.Contains("href=\"@($\"journal/{item.Id}\")\"", list);
        Assert.Contains("JournalDisplay.ListDateTime(item.OccurredAtUtc, today, TimeZoneInfo.Local, CultureInfo.CurrentCulture)", list);
        Assert.Contains("@if (!string.IsNullOrWhiteSpace(item.Title))", list);
        Assert.Contains("JournalDisplay.Preview(item.Content)", list);

        // Pages are appended exactly as received: the client never re-sorts.
        Assert.Contains("JournalApi.GetPageAsync(nextCursor)", list);
        Assert.Contains("items.AddRange(result.Value!.Items);", list);
        Assert.DoesNotMatch(new Regex(@"\b(OrderBy|OrderByDescending|Sort|Reverse)\b"), list);
    }

    // ---- New / Edit ----

    [Fact]
    public void New_DefaultsToNow_AndCreates()
    {
        var page = New();

        Assert.Contains("JournalEntryDraft.ForNew(DateTime.Now)", page);
        Assert.Contains("JournalApi.CreateAsync(JournalEntryDraft.ToCreateRequest(values))", page);
        Assert.Contains("CancelHref=\"journal\"", page);
    }

    [Fact]
    public void Edit_LoadsInLocalTime_AndUpdates_WithNotFoundAndErrorStates()
    {
        var page = Edit();

        Assert.Contains("JournalEntryDraft.From(result.Value!, TimeZoneInfo.Local)", page);
        Assert.Contains("JournalApi.UpdateAsync(EntryId, JournalEntryDraft.ToUpdateRequest(values))", page);
        Assert.Contains("JournalApiClient.IsNotFound(result)", page);
        Assert.Contains("@JournalApiClient.NotFoundMessage", page);
        Assert.Contains("Back to journal", page);
        Assert.Contains("Retry", page);
    }

    [Fact]
    public void Form_HasTheThreeFields_ConvertsLocalTime_AndNeverTruncates()
    {
        var form = Form();

        Assert.Contains("type=\"datetime-local\"", form);
        Assert.Contains("Occurred at", form);
        Assert.Contains("Title (optional)", form);
        Assert.Contains("Content", form);
        Assert.Contains("Draft.TryBuild(TimeZoneInfo.Local, out var values, out var problems)", form);
        Assert.Contains("JournalEntryDraft.TitleMaxLength", form);
        Assert.Contains("JournalEntryDraft.ContentMaxLength", form);
        Assert.DoesNotContain("maxlength=", form, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Navigation.NavigateTo($\"journal/{result.Value!.Id}\", new NavigationOptions { ReplaceHistoryEntry = true })", form);
    }

    // ---- Detail / delete ----

    [Fact]
    public void Detail_RendersTitleTimeAndFullContent_WithEditAction()
    {
        var detail = Detail();

        Assert.Contains("Loading entry...", detail);
        Assert.Contains("JournalApi.GetAsync(EntryId)", detail);
        Assert.Contains("@if (!string.IsNullOrWhiteSpace(entry.Title))", detail);
        Assert.Contains("JournalDisplay.LongDateTime(entry.OccurredAtUtc, TimeZoneInfo.Local, CultureInfo.CurrentCulture)", detail);
        Assert.Contains("<p class=\"journal-content mb-0 text-break\">@entry.Content</p>", detail);
        Assert.Contains("white-space: pre-wrap", File.ReadAllText(Path.Combine(ComponentsRoot(), "Pages", "Journal", "JournalEntryDetail.razor.css")));
        Assert.Contains("href=\"@($\"journal/{entry.Id}/edit\")\"", detail);
    }

    [Fact]
    public void Detail_ShowsNotFound_WithoutRetry_AndOtherErrors_WithRetry()
    {
        var detail = Detail();
        var notFound = detail.IndexOf("else if (entry is null && notFound)", StringComparison.Ordinal);
        var otherError = detail.IndexOf("else if (entry is null)", notFound + 1, StringComparison.Ordinal);

        Assert.True(notFound > 0 && otherError > notFound);
        Assert.Contains("@JournalApiClient.NotFoundMessage", detail[notFound..otherError]);
        Assert.Contains("Back to journal", detail[notFound..otherError]);
        Assert.DoesNotContain("Retry", detail[notFound..otherError]);
        Assert.Contains("Retry", detail[otherError..]);
    }

    [Fact]
    public void Delete_RequiresExplicitConfirmation_ThenReturnsToTheTimeline()
    {
        var detail = Detail();
        var ask = detail.IndexOf("@onclick=\"() => confirmingDelete = true\">Delete entry</button>", StringComparison.Ordinal);
        var confirmation = detail.IndexOf("Delete this journal entry? This can't be undone.", StringComparison.Ordinal);
        var confirm = detail.IndexOf("@onclick=\"DeleteAsync\"", StringComparison.Ordinal);

        // "Delete entry" only opens the confirmation; the API call is behind its own Delete button.
        Assert.True(ask > 0 && confirmation > ask && confirm > confirmation);
        Assert.Single(Regex.Matches(detail, Regex.Escape("@onclick=\"DeleteAsync\"")));
        Assert.Contains("CancelDelete", detail);
        Assert.Contains("JournalApi.DeleteAsync(entry.Id)", detail);
        Assert.Contains("Navigation.NavigateTo(\"journal\", new NavigationOptions { ReplaceHistoryEntry = true })", detail);
    }

    // PD: JRN-002 has no AI, tags, mood or attachments.
    [Fact]
    public void Pages_HaveNoAiOrOutOfScopeFields()
    {
        foreach (var page in new[] { List(), New(), Detail(), Edit(), Form() })
        {
            Assert.DoesNotMatch(new Regex(@"\bAI\b|semantic|embedding|\btags?\b|\bmood\b|attachment", RegexOptions.IgnoreCase), page);
        }
    }

    // ---- API client ----

    [Fact]
    public async Task Client_UsesTheJournalRoutes_WithLimitAndCursor()
    {
        var requests = new List<string>();
        var id = Guid.CreateVersion7();
        var entry = Entry(id, "Title");
        var client = new JournalApiClient(Http(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");

            return request.Method.Method switch
            {
                "GET" when request.RequestUri.AbsolutePath == "/api/journal" => Json(new JournalEntryPageResponse([entry], null)),
                "POST" => Json(entry, HttpStatusCode.Created),
                "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => Json(entry)
            };
        }));

        await client.GetPageAsync(null);
        await client.GetPageAsync("638000000000000000_638000000000000000_abc+/=");
        await client.GetPageAsync(null, limit: 5);
        await client.GetAsync(id);
        await client.CreateAsync(new CreateJournalEntryRequest(entry.OccurredAtUtc, null, "x"));
        await client.UpdateAsync(id, new UpdateJournalEntryRequest(entry.OccurredAtUtc, null, "x"));
        var deleted = await client.DeleteAsync(id);

        Assert.True(deleted.IsSuccess);
        Assert.Equal(
            [
                "GET /api/journal?limit=20",
                "GET /api/journal?limit=20&cursor=638000000000000000_638000000000000000_abc%2B%2F%3D",
                "GET /api/journal?limit=5",
                $"GET /api/journal/{id}",
                "POST /api/journal",
                $"PUT /api/journal/{id}",
                $"DELETE /api/journal/{id}"
            ],
            requests);
    }

    [Fact]
    public async Task Client_WalksPages_FollowingNextCursor_InTheApiOrder()
    {
        var first = new[] { Entry(Guid.CreateVersion7(), "B"), Entry(Guid.CreateVersion7(), "A") };
        var second = new[] { Entry(Guid.CreateVersion7(), "Z") };
        var client = new JournalApiClient(Http(request =>
            request.RequestUri!.Query.Contains("cursor=next", StringComparison.Ordinal)
                ? Json(new JournalEntryPageResponse(second, null))
                : Json(new JournalEntryPageResponse(first, "next"))));

        var items = new List<JournalEntryResponse>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await client.GetPageAsync(cursor);
            items.AddRange(page.Value!.Items);
            cursor = page.Value.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(2, pages);
        Assert.Equal(["B", "A", "Z"], items.Select(item => item.Title));
    }

    [Fact]
    public async Task Client_SendsTheCreateAndUpdatePayloads()
    {
        var bodies = new List<string>();
        var id = Guid.CreateVersion7();
        var client = new JournalApiClient(Http(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

            return Json(Entry(id, null));
        }));
        var occurredAt = new DateTimeOffset(2026, 10, 7, 6, 30, 0, TimeSpan.Zero);

        await client.CreateAsync(new CreateJournalEntryRequest(occurredAt, "Morning", "Slept well."));
        await client.UpdateAsync(id, new UpdateJournalEntryRequest(occurredAt, null, "Slept well."));

        using var create = JsonDocument.Parse(bodies[0]);
        using var update = JsonDocument.Parse(bodies[1]);

        Assert.Equal("2026-10-07T06:30:00+00:00", create.RootElement.GetProperty("occurredAtUtc").GetString());
        Assert.Equal("Morning", create.RootElement.GetProperty("title").GetString());
        Assert.Equal("Slept well.", create.RootElement.GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, update.RootElement.GetProperty("title").ValueKind);
        Assert.False(create.RootElement.TryGetProperty("userId", out _));
    }

    [Fact]
    public async Task Client_ReportsValidationNotFoundServerAndTransportFailures()
    {
        var id = Guid.CreateVersion7();
        var validation = new JournalApiClient(Http(_ => Problem(HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"title":["Title must be at most 200 characters."]}}""")));
        var missing = new JournalApiClient(Http(_ => Problem(HttpStatusCode.NotFound, """{"title":"Journal entry not found.","status":404}""")));
        var failing = new JournalApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var unreachable = new JournalApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))) { BaseAddress = new Uri("https://lifeos.test/") });

        var invalid = await validation.CreateAsync(new CreateJournalEntryRequest(DateTimeOffset.UnixEpoch, new string('t', 201), "x"));
        var notFound = await missing.GetAsync(id);
        var notFoundUpdate = await missing.UpdateAsync(id, new UpdateJournalEntryRequest(DateTimeOffset.UnixEpoch, null, "x"));
        var serverError = await failing.GetPageAsync(null);
        var offline = await unreachable.GetAsync(id);

        Assert.Equal(["Title must be at most 200 characters."], invalid.Errors);
        Assert.False(JournalApiClient.IsNotFound(invalid));
        Assert.Equal([JournalApiClient.NotFoundMessage], notFound.Errors);
        Assert.True(JournalApiClient.IsNotFound(notFound));
        Assert.True(JournalApiClient.IsNotFound(notFoundUpdate));
        Assert.Equal(["The LifeOS API returned an unexpected response (500)."], serverError.Errors);
        Assert.Equal([ApiErrors.UnreachableMessage], offline.Errors);
        Assert.False(JournalApiClient.IsNotFound(offline));
    }

    [Fact]
    public async Task Client_Delete_TreatsAnAlreadyMissingEntryAsDeleted_ButReportsOtherFailures()
    {
        var gone = await new JournalApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).DeleteAsync(Guid.CreateVersion7());
        var failed = await new JournalApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).DeleteAsync(Guid.CreateVersion7());

        Assert.True(gone.IsSuccess);
        Assert.False(failed.IsSuccess);
    }

    // ---- Draft: create / edit payloads and validation ----

    [Fact]
    public void New_DefaultsToTheLocalNow_AndConvertsItToUtc()
    {
        var draft = JournalEntryDraft.ForNew(new DateTime(2026, 10, 7, 8, 30, 45));
        draft.Content = "  Slept well.\n\nLong walk.  ";
        draft.Title = "  Morning  ";

        Assert.Equal("2026-10-07T08:30", draft.OccurredAtLocalText);
        Assert.True(draft.TryBuild(PlusTwo, out var values, out var errors), string.Join(" ", errors));

        var request = JournalEntryDraft.ToCreateRequest(values);

        Assert.Equal(new DateTimeOffset(2026, 10, 7, 6, 30, 0, TimeSpan.Zero), request.OccurredAtUtc);
        Assert.Equal(TimeSpan.Zero, request.OccurredAtUtc!.Value.Offset);
        Assert.Equal("Morning", request.Title);
        Assert.Equal("Slept well.\n\nLong walk.", request.Content);
    }

    [Fact]
    public void ChangedOccurredAt_IsReadAsLocalWallClockTime()
    {
        var draft = JournalEntryDraft.ForNew(new DateTime(2026, 10, 7, 8, 30, 0));
        draft.Content = "x";

        draft.SetOccurredAtFromPicker("2026-01-15T23:10:59.123");
        Assert.Equal("2026-01-15T23:10", draft.OccurredAtLocalText);
        Assert.True(draft.TryBuild(PlusTwo, out var values, out _));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 21, 10, 0, TimeSpan.Zero), values.OccurredAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2026-10-07T08:30Z")]
    public void InvalidOccurredAt_IsAnError(string raw)
    {
        var draft = JournalEntryDraft.ForNew(DateTime.Now);
        draft.Content = "x";
        draft.SetOccurredAtFromPicker(raw);

        Assert.False(draft.TryBuild(PlusTwo, out _, out var errors));
        Assert.Contains(JournalEntryDraft.InvalidDateTimeMessage, errors);
    }

    [Fact]
    public void ALocalTimeSkippedByDaylightSaving_IsAnError()
    {
        var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        var draft = JournalEntryDraft.ForNew(new DateTime(2026, 3, 29, 2, 30, 0));
        draft.Content = "x";

        Assert.False(draft.TryBuild(rome, out _, out var errors));
        Assert.Contains("daylight saving", Assert.Single(errors));
    }

    [Fact]
    public void Edit_StartsFromTheEntryInLocalTime_AndKeepsTheExactInstantWhileUnchanged()
    {
        var stored = new DateTimeOffset(2026, 10, 7, 6, 30, 12, TimeSpan.Zero).AddTicks(1230);
        var draft = JournalEntryDraft.From(Entry(Guid.CreateVersion7(), "Morning", stored, "Slept well."), PlusTwo);

        Assert.Equal("2026-10-07T08:30", draft.OccurredAtLocalText);
        Assert.Equal("Morning", draft.Title);
        Assert.Equal("Slept well.", draft.Content);

        draft.Content = "Slept very well.";
        Assert.True(draft.TryBuild(PlusTwo, out var values, out _));

        var request = JournalEntryDraft.ToUpdateRequest(values);
        Assert.Equal(stored, request.OccurredAtUtc);
        Assert.Equal("Morning", request.Title);
        Assert.Equal("Slept very well.", request.Content);

        draft.SetOccurredAtFromPicker("2026-10-06T21:00");
        Assert.True(draft.TryBuild(PlusTwo, out values, out _));
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 19, 0, 0, TimeSpan.Zero), values.OccurredAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Edit_BlankTitle_IsSentAsNull_WhichRemovesIt(string title)
    {
        var draft = JournalEntryDraft.From(Entry(Guid.CreateVersion7(), "Morning"), PlusTwo);
        draft.Title = title;

        Assert.True(draft.TryBuild(PlusTwo, out var values, out _));
        Assert.Null(JournalEntryDraft.ToUpdateRequest(values).Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Content_IsRequired(string content)
    {
        var draft = JournalEntryDraft.ForNew(DateTime.Now);
        draft.Content = content;

        Assert.False(draft.TryBuild(PlusTwo, out _, out var errors));
        Assert.Equal([JournalEntryDraft.ContentRequiredMessage], errors);
    }

    [Fact]
    public void Limits_AreCheckedAfterTrimming_AtTheApiMaximums()
    {
        var draft = JournalEntryDraft.ForNew(DateTime.Now);
        draft.Title = "  " + new string('t', 200) + "  ";
        draft.Content = " " + new string('c', 20_000) + "\n";

        Assert.Equal(200, draft.TitleLength);
        Assert.Equal(20_000, draft.ContentLength);
        Assert.True(draft.TryBuild(PlusTwo, out var values, out _));
        Assert.Equal(200, values.Title!.Length);
        Assert.Equal(20_000, values.Content.Length);
    }

    [Fact]
    public void OverLongText_IsRejected_NeverTruncated()
    {
        var title = new string('t', 201);
        var content = new string('c', 20_001);
        var draft = JournalEntryDraft.ForNew(DateTime.Now);
        draft.Title = title;
        draft.Content = content;

        Assert.False(draft.TryBuild(PlusTwo, out var values, out var errors));
        Assert.Null(values);
        Assert.Equal(
            ["Title must be at most 200 characters (201 now).", "Content must be at most 20,000 characters (20,001 now)."],
            errors);
        Assert.Equal(title, draft.Title);
        Assert.Equal(content, draft.Content);
    }

    [Fact]
    public void ClientLimits_MatchTheApi()
    {
        Assert.Equal(LifeOS.Domain.Journal.JournalEntry.TitleMaxLength, JournalEntryDraft.TitleMaxLength);
        Assert.Equal(LifeOS.Domain.Journal.JournalEntry.ContentMaxLength, JournalEntryDraft.ContentMaxLength);
    }

    // ---- Display ----

    [Fact]
    public void OccurredAt_IsShownInLocalTime()
    {
        var occurredAt = new DateTimeOffset(2026, 10, 6, 22, 30, 0, TimeSpan.Zero);
        var culture = CultureInfo.InvariantCulture;

        // 22:30 UTC is already the next local day at +02:00.
        Assert.Equal("Today 00:30", JournalDisplay.ListDateTime(occurredAt, new DateTime(2026, 10, 7), PlusTwo, culture));
        Assert.Equal("Yesterday 00:30", JournalDisplay.ListDateTime(occurredAt, new DateTime(2026, 10, 8), PlusTwo, culture));
        Assert.Equal("7 October · 00:30", JournalDisplay.ListDateTime(occurredAt, new DateTime(2026, 12, 1), PlusTwo, culture));
        Assert.Equal("7 October 2026 · 00:30", JournalDisplay.LongDateTime(occurredAt, PlusTwo, culture));
        Assert.Equal("6 October 2026 · 22:30", JournalDisplay.LongDateTime(occurredAt, TimeZoneInfo.Utc, culture));
    }

    [Fact]
    public void Preview_CollapsesWhitespace_AndShortensLongContent()
    {
        Assert.Equal("Slept well. Long walk.", JournalDisplay.Preview("  Slept well.\n\n\tLong walk.  "));
        Assert.Equal("abc", JournalDisplay.Preview("abc"));

        var exact = new string('a', JournalDisplay.PreviewLength);
        Assert.Equal(exact, JournalDisplay.Preview(exact));

        var words = string.Join(' ', Enumerable.Repeat("word", 60));
        var preview = JournalDisplay.Preview(words);
        Assert.EndsWith("word…", preview);
        Assert.True(preview.Length <= JournalDisplay.PreviewLength + 1);

        var unbroken = JournalDisplay.Preview(new string('x', 500));
        Assert.Equal(new string('x', JournalDisplay.PreviewLength) + "…", unbroken);
    }

    // ---- Helpers ----

    private static JournalEntryResponse Entry(Guid id, string? title, DateTimeOffset? occurredAt = null, string content = "Content") =>
        new(id, occurredAt ?? new DateTimeOffset(2026, 10, 7, 6, 30, 0, TimeSpan.Zero), title, content, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static string List() => Source("Pages", Path.Combine("Journal", "Journal.razor"));

    private static string New() => Source("Pages", Path.Combine("Journal", "NewJournalEntry.razor"));

    private static string Detail() => Source("Pages", Path.Combine("Journal", "JournalEntryDetail.razor"));

    private static string Edit() => Source("Pages", Path.Combine("Journal", "EditJournalEntry.razor"));

    private static string Form() => Source("Journal", "JournalEntryForm.razor");

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new StubHandler(request => Task.FromResult(send(request)))) { BaseAddress = new Uri("https://lifeos.test/") };

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/problem+json") };

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
