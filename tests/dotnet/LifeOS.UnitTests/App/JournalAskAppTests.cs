using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LifeOS.App.Services;
using LifeOS.App.Services.Journal;
using LifeOS.Application.Memory;
using LifeOS.Contracts.Memory;

namespace LifeOS.UnitTests.App;

// JRN-003 app: Ask Journal. The Journal Memory client (routes, payloads, failures), the index-before-ask
// flow (bounded sync, no-progress stop, never an ask over a knowingly incomplete index) and the page,
// checked by source as in JournalAppTests. No network, provider or database call.
public class JournalAskAppTests
{
    // ---- Navigation ----

    [Fact]
    public void AskPage_IsRouted_UnderJournal_AndTheTimelineLinksToIt()
    {
        Assert.StartsWith("@page \"/journal/ask\"", Page());
        Assert.Contains("<PageHeader Title=\"Ask Journal\" BackHref=\"journal\" />", Page());

        var list = Source("Pages", Path.Combine("Journal", "Journal.razor"));
        Assert.Contains("<a href=\"journal/ask\"", list);
        Assert.Contains(">Ask Journal</a>", list);
        Assert.True(list.IndexOf("journal/new", StringComparison.Ordinal) < list.IndexOf("journal/ask", StringComparison.Ordinal));

        // Not in the dock or header; More → Journal → Ask Journal.
        Assert.DoesNotContain("journal", Source("Layout", "BottomDock.razor"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journal", Source("Layout", "AppHeader.razor"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Client_IsRegisteredOnTheAuthorizedPipeline_WithTheJournalMemoryTimeout()
    {
        var program = File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "MauiProgram.cs"));

        Assert.Matches(new Regex(@"new LifeOS\.App\.Services\.Journal\.JournalMemoryApiClient\(\s*CreateAuthorizedHttpClient\(services, ApiTimeouts\.JournalMemory\)\)"), program);
        Assert.Single(Regex.Matches(program, Regex.Escape("ApiTimeouts.JournalMemory")));
        Assert.Contains("new AuthorizationMessageHandler(services.GetRequiredService<TokenSession>(), new HttpClientHandler())", program);
    }

    // ---- Page ----

    [Fact]
    public void Page_HasALabelledQuestion_AndAnAskButton_DisabledWhileBusy()
    {
        var page = Page();

        Assert.Contains("Ask about things you've written in your journal.", page);
        Assert.Contains("<label for=\"journal-ask-question\" class=\"form-label\">Your question</label>", page);
        Assert.Contains("<textarea id=\"journal-ask-question\"", page);
        Assert.Contains("@bind=\"flow.Question\"", page);
        Assert.Contains("<button type=\"submit\" class=\"btn btn-primary w-100\" disabled=\"@flow.IsBusy\">", page);
        Assert.Contains("\"Ask\"", page);
        Assert.Contains("JournalAskFlow.MaxQuestionLength", page);
        Assert.DoesNotContain("maxlength=", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_ShowsPreparingSearchingAnswerInsufficientAndRetryStates()
    {
        var page = Page();

        Assert.Contains("case JournalAskState.Preparing:", page);
        Assert.Contains("Preparing your journal...", page);
        Assert.Contains("PendingText(flow.PendingEntries)", page);
        Assert.Contains("case JournalAskState.Asking:", page);
        Assert.Contains("Searching your journal...", page);
        Assert.Contains("case JournalAskState.Answered:", page);
        Assert.Contains("@flow.Result!.Answer", page);
        Assert.Contains("case JournalAskState.InsufficientEvidence:", page);
        Assert.Contains("I couldn't find enough in your journal to answer that.", page);
        Assert.Contains("case JournalAskState.Error:", page);
        Assert.Contains("@flow.ErrorMessage", page);
        Assert.Contains("@onclick=\"AskAsync\">Retry</button>", page);
        Assert.Contains("aria-live=\"polite\"", page);
        Assert.Contains("white-space: pre-wrap", File.ReadAllText(Path.Combine(ComponentsRoot(), "Pages", "Journal", "AskJournal.razor.css")));

        // The insufficient-evidence state shows only the fixed message, never answer text.
        var insufficient = Between(page, "case JournalAskState.InsufficientEvidence:", "case JournalAskState.Error:");
        Assert.DoesNotContain("flow.Result", insufficient);
    }

    [Fact]
    public void Page_ListsSources_FromTheApiCitations_LinkingToTheEntry()
    {
        var page = Page();

        Assert.Contains(">Sources</h2>", page);
        Assert.Contains("@if (flow.Result is { Citations.Count: > 0 } result)", page);
        Assert.Contains("@foreach (var citation in result.Citations)", page);
        Assert.Contains("href=\"@($\"journal/{citation.EntryId}\")\"", page);
        Assert.Contains("JournalDisplay.ListDateTime(citation.OccurredAtUtc, today, TimeZoneInfo.Local, CultureInfo.CurrentCulture)", page);
        Assert.Contains("@if (!string.IsNullOrWhiteSpace(citation.Title))", page);
        Assert.Contains("@citation.Excerpt", page);
        Assert.DoesNotMatch(new Regex(@"\b(OrderBy|OrderByDescending|Sort|Reverse|Distinct)\b"), page);
    }

    [Fact]
    public void Page_IsNotAChat_AndShowsNoProviderOrRankingInternals()
    {
        var page = Page();

        Assert.DoesNotMatch(new Regex(@"openai|groq|gemini|gpt|model|provider|prompt|embedding|chat|history|conversation|thread", RegexOptions.IgnoreCase), page);
        Assert.DoesNotMatch(new Regex(@"\bScore\b|VectorRank|LexicalRank|\.Rank\b|ChunkOrdinal|RetrievalVersion|ResponseId", RegexOptions.IgnoreCase), page);
        Assert.DoesNotContain("<select", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@foreach (var", Between(page, "<form", "</form>"));
    }

    [Fact]
    public void QuestionAndAnswer_StayInPageState_NeverPersistedOrLogged()
    {
        foreach (var source in new[] { Page(), AppSource("JournalAskFlow.cs"), AppSource("JournalMemoryApiClient.cs") })
        {
            Assert.DoesNotMatch(new Regex(@"Preferences|SecureStorage|localStorage|sessionStorage|IJSRuntime|File\.Write|ILogger|Console\.|Debug\.Write|Trace\.", RegexOptions.None), source);
        }

        // A new flow per page instance; the flow is not a DI singleton.
        Assert.Contains("flow = new JournalAskFlow(MemoryApi,", Page());
        Assert.DoesNotContain("JournalAskFlow", File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "MauiProgram.cs")));
        Assert.Contains("lifetime.Cancel();", Page());
    }

    [Fact]
    public void ClientLimits_MatchTheApi()
    {
        Assert.Equal(JournalMemoryPolicy.MaxQuestionLength, JournalAskFlow.MaxQuestionLength);
        Assert.Equal(3, JournalAskFlow.SyncBatchSize);
        Assert.InRange(JournalAskFlow.SyncBatchSize, 1, JournalMemoryPolicy.MaxSyncLimit);
        Assert.Equal(JournalMemoryValidation.Answered, JournalAskFlow.AnsweredStatus);
        Assert.Equal(JournalMemoryValidation.InsufficientEvidence, JournalAskFlow.InsufficientEvidenceStatus);
    }

    // ---- Client ----

    [Fact]
    public async Task Client_UsesTheMemoryRoutes_AndPayloads()
    {
        var requests = new List<string>();
        var client = new JournalMemoryApiClient(Http(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {body}".TrimEnd());

            return request.RequestUri.AbsolutePath switch
            {
                "/api/journal/memory/status" => Json(Status(0)),
                "/api/journal/memory/sync" => Json(Sync(1, 0)),
                _ => Json(Answered())
            };
        }));

        await client.GetStatusAsync();
        await client.SyncAsync(3);
        await client.AskAsync("Dove ho lasciato le chiavi rosse?");

        Assert.Equal("GET /api/journal/memory/status", requests[0]);
        Assert.StartsWith("POST /api/journal/memory/sync ", requests[1]);
        Assert.StartsWith("POST /api/journal/memory/ask ", requests[2]);

        using var sync = JsonDocument.Parse(requests[1]["POST /api/journal/memory/sync ".Length..]);
        using var ask = JsonDocument.Parse(requests[2]["POST /api/journal/memory/ask ".Length..]);
        Assert.Equal(3, sync.RootElement.GetProperty("limit").GetInt32());
        Assert.Single(sync.RootElement.EnumerateObject());
        Assert.Equal("Dove ho lasciato le chiavi rosse?", ask.RootElement.GetProperty("question").GetString());
        Assert.Single(ask.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task Client_ReadsStatusSyncAndAnswers_PreservingCitationsExactly()
    {
        var first = Citation("Test Ask Journal", "Oggi ho lasciato le chiavi rosse nel cassetto della scrivania.");
        var second = Citation(null, "Seconda fonte…", chunkOrdinal: 2);
        var client = new JournalMemoryApiClient(Http(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/journal/memory/status" => Raw(
                """{"indexedEntries":4,"pendingEntries":2,"indexIncomplete":true,"index":{"chunkingVersion":"journal-chunking-v1","embeddingProvider":"openai","embeddingModel":"text-embedding-3-small","embeddingDimensions":1536,"retrievalVersion":"journal-retrieval-v2"}}"""),
            "/api/journal/memory/sync" => Raw("""{"claimed":3,"indexed":2,"staleDiscarded":1,"failed":0,"pendingRemaining":0,"more":false}"""),
            _ => Json(Answered(first, second))
        }));

        var status = await client.GetStatusAsync();
        var sync = await client.SyncAsync(3);
        var answer = await client.AskAsync("q");

        Assert.Equal(4, status.Value!.IndexedEntries);
        Assert.Equal(2, status.Value.PendingEntries);
        Assert.True(status.Value.IndexIncomplete);
        Assert.Equal(new JournalMemorySyncResponse(3, 2, 1, 0, 0, false), sync.Value);
        Assert.Equal("answered", answer.Value!.Status);
        Assert.Equal("Nel cassetto della scrivania.", answer.Value.Answer);
        Assert.Equal([first, second], answer.Value.Citations);
    }

    [Fact]
    public async Task Client_ReadsInsufficientEvidence()
    {
        var client = new JournalMemoryApiClient(Http(_ => Json(Insufficient())));

        var answer = await client.AskAsync("Qual è il numero di telefono di Marco?");

        Assert.Equal("insufficient_evidence", answer.Value!.Status);
        Assert.Equal("", answer.Value.Answer);
        Assert.Empty(answer.Value.Citations);
    }

    [Fact]
    public async Task Client_ReportsValidationUnavailableFailedAndTransportErrors_WithoutStatusCodes()
    {
        var validation = await new JournalMemoryApiClient(Http(_ => Problem(HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"question":["Write a question."]}}"""))).AskAsync(" ");
        var syncLimit = await new JournalMemoryApiClient(Http(_ => Problem(HttpStatusCode.BadRequest,
            """{"status":400,"errors":{"limit":["The limit must be between 1 and 10."]}}"""))).SyncAsync(3);
        var failed = await new JournalMemoryApiClient(Http(_ => Problem(HttpStatusCode.BadGateway,
            """{"title":"Journal memory failed","detail":"Journal memory could not produce a valid result. Try again later.","status":502}"""))).AskAsync("q");
        var unavailable = await new JournalMemoryApiClient(Http(_ => Problem(HttpStatusCode.ServiceUnavailable,
            """{"title":"Journal memory unavailable","detail":"Journal memory is unavailable right now. Your journal is safe; try again later.","status":503}"""))).SyncAsync(3);
        var bareGateway = await new JournalMemoryApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html")
        })).AskAsync("q");
        var bareUnavailable = await new JournalMemoryApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).GetStatusAsync();
        var offline = await new JournalMemoryApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down")))
        {
            BaseAddress = new Uri("https://lifeos.test/")
        }).AskAsync("q");
        var timedOut = await new JournalMemoryApiClient(new HttpClient(new StubHandler(_ => throw new TaskCanceledException("timeout")))
        {
            BaseAddress = new Uri("https://lifeos.test/")
        }).SyncAsync(3);

        Assert.Equal(["Write a question."], validation.Errors);
        Assert.Equal(["The limit must be between 1 and 10."], syncLimit.Errors);
        Assert.Equal(["Journal memory could not produce a valid result. Try again later."], failed.Errors);
        Assert.Equal(["Journal memory is unavailable right now. Your journal is safe; try again later."], unavailable.Errors);
        Assert.Equal([JournalMemoryApiClient.UnavailableMessage], bareGateway.Errors);
        Assert.Equal([JournalMemoryApiClient.UnavailableMessage], bareUnavailable.Errors);
        Assert.Equal([ApiErrors.UnreachableMessage], offline.Errors);
        Assert.Equal([ApiErrors.UnreachableMessage], timedOut.Errors);

        foreach (var result in new[] { failed.Errors, unavailable.Errors, bareGateway.Errors, bareUnavailable.Errors, offline.Errors })
        {
            Assert.DoesNotMatch(new Regex(@"\b(4|5)\d\d\b"), string.Join(" ", result));
        }
    }

    [Fact]
    public async Task Client_LetsTheCallersCancellationPropagate()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new JournalMemoryApiClient(new HttpClient(new StubHandler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException();
        }))
        { BaseAddress = new Uri("https://lifeos.test/") });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AskAsync("q", cancellation.Token));
    }

    // ---- Index-before-ask flow ----

    [Fact]
    public async Task NothingPending_AsksWithoutSyncing()
    {
        var api = new FakeMemoryApi(pending: 0);
        var flow = Flow(api, "  Dove ho lasciato le chiavi rosse?  ");

        await flow.AskAsync();

        Assert.Equal(["status", "ask"], api.Calls);
        Assert.Equal(["Dove ho lasciato le chiavi rosse?"], api.Questions);
        Assert.Equal(JournalAskState.Answered, flow.State);
        Assert.Equal("  Dove ho lasciato le chiavi rosse?  ", flow.Question);
    }

    [Fact]
    public async Task OnePending_SyncsOnce_ThenAsks()
    {
        var api = new FakeMemoryApi(pending: 1);
        var flow = Flow(api);

        await flow.AskAsync();

        Assert.Equal(["status", "sync", "ask"], api.Calls);
        Assert.Equal([3], api.SyncLimits);
        Assert.Equal(JournalAskState.Answered, flow.State);
    }

    [Fact]
    public async Task MorePendingThanABatch_SyncsInBatchesOfThree_AndAsksOnlyAtZero()
    {
        var api = new FakeMemoryApi(pending: 8);
        var states = new List<(JournalAskState State, int Pending)>();
        JournalAskFlow? flow = null;
        flow = new JournalAskFlow(new JournalMemoryApiClient(api.Http()), () => states.Add((flow!.State, flow.PendingEntries))) { Question = "q" };

        await flow.AskAsync();

        Assert.Equal(["status", "sync", "sync", "sync", "ask"], api.Calls);
        Assert.Equal([3, 3, 3], api.SyncLimits);
        Assert.Equal(0, api.PendingAtAsk);
        Assert.Equal(JournalAskState.Answered, flow.State);
        Assert.Equal(
            [(JournalAskState.Asking, 0), (JournalAskState.Preparing, 8), (JournalAskState.Preparing, 5), (JournalAskState.Preparing, 2), (JournalAskState.Asking, 0)],
            states);
    }

    [Fact]
    public async Task ASyncThatReducesPending_Continues_EvenWithSomeFailedOrStaleEntries()
    {
        var api = new FakeMemoryApi(pending: 5)
        {
            SyncResults =
            {
                new JournalMemorySyncResponse(3, 1, 1, 1, 3, true),  // 1 failed, but 2 fewer pending
                new JournalMemorySyncResponse(3, 3, 0, 0, 0, false)
            }
        };
        var flow = Flow(api);

        await flow.AskAsync();

        Assert.Equal(["status", "sync", "sync", "ask"], api.Calls);
        Assert.Equal(JournalAskState.Answered, flow.State);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 4)]   // nothing claimed (e.g. every job leased)
    [InlineData(3, 0, 0, 3, 4)]   // every claimed entry failed
    [InlineData(1, 1, 0, 0, 4)]   // indexed one, but pending did not go down
    [InlineData(1, 1, 0, 0, 5)]   // pending went up
    [InlineData(0, 0, 0, 0, 3)]   // pending went down without our progress
    public async Task ASyncWithoutProgress_Stops_AndNeverAsks(int claimed, int indexed, int stale, int failed, int remaining)
    {
        var api = new FakeMemoryApi(pending: 4)
        {
            SyncResults = { new JournalMemorySyncResponse(claimed, indexed, stale, failed, remaining, remaining > 0) }
        };
        var flow = Flow(api);

        await flow.AskAsync();

        Assert.Equal(["status", "sync"], api.Calls);
        Assert.Equal(JournalAskState.Error, flow.State);
        Assert.Equal(JournalAskFlow.PreparationStalledMessage, flow.ErrorMessage);
        Assert.Null(flow.Result);
        Assert.Equal("q", flow.Question);
    }

    [Fact]
    public async Task AStuckIndex_NeverLoopsForever()
    {
        // The API keeps claiming and "indexing" but pending never moves.
        var api = new FakeMemoryApi(pending: 3) { StuckSync = new JournalMemorySyncResponse(3, 3, 0, 0, 3, true) };
        var flow = Flow(api);

        await flow.AskAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["status", "sync"], api.Calls);
        Assert.Equal(JournalAskState.Error, flow.State);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("sync")]
    [InlineData("ask")]
    public async Task AnUnavailableStep_IsARetryableError_AndRetryRunsTheWholeFlowAgain(string failing)
    {
        var api = new FakeMemoryApi(pending: 2) { Failing = failing };
        var flow = Flow(api);

        await flow.AskAsync();

        Assert.Equal(JournalAskState.Error, flow.State);
        Assert.Equal("Journal memory is unavailable right now. Your journal is safe; try again later.", flow.ErrorMessage);
        Assert.Null(flow.Result);
        Assert.Equal(failing == "ask" ? 1 : 0, api.Calls.Count(call => call == "ask"));
        Assert.Equal("q", flow.Question);

        api.Failing = null;
        api.Calls.Clear();
        await flow.AskAsync();

        Assert.Equal(JournalAskState.Answered, flow.State);
        Assert.Null(flow.ErrorMessage);
        Assert.Equal("ask", api.Calls[^1]);
    }

    [Fact]
    public async Task InsufficientEvidence_IsShownAsSuch_WithNoAnswerOfItsOwn()
    {
        var api = new FakeMemoryApi(pending: 0) { Answer = Insufficient() };
        var flow = Flow(api);

        await flow.AskAsync();

        Assert.Equal(JournalAskState.InsufficientEvidence, flow.State);
        Assert.Equal("insufficient_evidence", flow.Result!.Status);
        Assert.Equal("", flow.Result.Answer);
        Assert.Empty(flow.Result.Citations);
        Assert.Equal(1, api.Calls.Count(call => call == "ask"));
    }

    [Fact]
    public async Task AnEmptyJournal_StillAsks_AndShowsTheBackendsAnswer()
    {
        var api = new FakeMemoryApi(pending: 0, indexed: 0) { Answer = Insufficient() };
        var flow = Flow(api);

        await flow.AskAsync();

        Assert.Equal(["status", "ask"], api.Calls);
        Assert.Equal(JournalAskState.InsufficientEvidence, flow.State);
    }

    [Fact]
    public async Task AnAnswer_KeepsTheApiCitationsExactly()
    {
        var citations = new[] { Citation("B", "second"), Citation("A", "first"), Citation("B", "second, again", chunkOrdinal: 1) };
        var api = new FakeMemoryApi(pending: 0) { Answer = Answered(citations) };
        var flow = Flow(api);

        await flow.AskAsync();

        Assert.Equal(JournalAskState.Answered, flow.State);
        Assert.Equal(api.Answer.Answer, flow.Result!.Answer);
        Assert.Equal(citations, flow.Result.Citations);
    }

    [Fact]
    public async Task AnUnknownAnswerStatus_IsAnError_NotAnAnswer()
    {
        var api = new FakeMemoryApi(pending: 0) { Answer = Answered() with { Status = "maybe" } };
        var flow = Flow(api);

        await flow.AskAsync();

        Assert.Equal(JournalAskState.Error, flow.State);
        Assert.Equal(JournalAskFlow.UnexpectedAnswerMessage, flow.ErrorMessage);
        Assert.Null(flow.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task ABlankQuestion_CallsNothing(string question)
    {
        var api = new FakeMemoryApi(pending: 2);
        var flow = Flow(api, question);

        await flow.AskAsync();

        Assert.Empty(api.Calls);
        Assert.Equal(JournalAskState.Idle, flow.State);
        Assert.Equal(JournalAskFlow.QuestionRequiredMessage, flow.ValidationMessage);
    }

    [Fact]
    public async Task ATooLongQuestion_IsRejected_NeverTruncated()
    {
        var api = new FakeMemoryApi(pending: 0);
        var question = " " + new string('q', 501);
        var flow = Flow(api, question);

        Assert.Null(JournalAskFlow.Validate(new string('q', 500)));
        await flow.AskAsync();

        Assert.Empty(api.Calls);
        Assert.Equal("The question must be at most 500 characters (501 now).", flow.ValidationMessage);
        Assert.Equal(question, flow.Question);
        Assert.Equal(501, flow.QuestionLength);
    }

    [Fact]
    public async Task ANewQuestion_ClearsThePreviousAnswer()
    {
        var api = new FakeMemoryApi(pending: 0);
        var flow = Flow(api);

        await flow.AskAsync();
        Assert.NotNull(flow.Result);

        api.Failing = "ask";
        await flow.AskAsync();

        Assert.Null(flow.Result);
        Assert.Equal(JournalAskState.Error, flow.State);
    }

    [Fact]
    public async Task LeavingThePage_StopsPreparing_AndNeverAsks()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeMemoryApi(pending: 6) { OnSync = cancellation.Cancel };
        var flow = Flow(api);

        await flow.AskAsync(cancellation.Token);

        Assert.Equal(["status", "sync"], api.Calls);
        Assert.Equal(JournalAskState.Idle, flow.State);
        Assert.Null(flow.Result);
    }

    // ---- Helpers ----

    private static JournalAskFlow Flow(FakeMemoryApi api, string question = "q") =>
        new(new JournalMemoryApiClient(api.Http())) { Question = question };

    private static JournalMemoryStatusResponse Status(int pending, int indexed = 1) =>
        new(indexed, pending, pending > 0, new JournalMemoryIndexResponse("journal-chunking-v1", "openai", "text-embedding-3-small", 1536, "journal-retrieval-v2"));

    private static JournalMemorySyncResponse Sync(int indexed, int remaining) => new(indexed, indexed, 0, 0, remaining, remaining > 0);

    private static JournalMemoryCitationResponse Citation(string? title, string excerpt, int chunkOrdinal = 0) =>
        new(Guid.CreateVersion7(), new DateTimeOffset(2026, 10, 11, 7, 30, 0, TimeSpan.Zero), title, chunkOrdinal, excerpt);

    private static JournalMemoryAnswerResponse Answered(params JournalMemoryCitationResponse[] citations) =>
        new("answered", "Nel cassetto della scrivania.", citations, false, 0, "journal-retrieval-v2");

    private static JournalMemoryAnswerResponse Insufficient() =>
        new("insufficient_evidence", "", [], false, 0, "journal-retrieval-v2");

    private static string Page() => Source("Pages", Path.Combine("Journal", "AskJournal.razor"));

    private static string AppSource(string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "Services", "Journal", fileName));

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from);
        return text[from..to];
    }

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new StubHandler(request => Task.FromResult(send(request)))) { BaseAddress = new Uri("https://lifeos.test/") };

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Raw(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/problem+json") };

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));

    // The Journal Memory API in memory: sync indexes up to `limit` pending entries unless a scripted
    // result (SyncResults, then StuckSync) is given; Failing makes one route answer 503.
    private sealed class FakeMemoryApi(int pending, int indexed = 1)
    {
        private int _pending = pending;
        private int _syncCount;

        public List<string> Calls { get; } = [];

        public List<int> SyncLimits { get; } = [];

        public List<string> Questions { get; } = [];

        public List<JournalMemorySyncResponse> SyncResults { get; } = [];

        public JournalMemorySyncResponse? StuckSync { get; init; }

        public JournalMemoryAnswerResponse Answer { get; init; } = Answered(Citation("Test Ask Journal", "Oggi ho lasciato le chiavi rosse nel cassetto della scrivania."));

        public string? Failing { get; set; }

        public Action? OnSync { get; init; }

        public int? PendingAtAsk { get; private set; }

        public HttpClient Http() => new(new StubHandler(SendAsync)) { BaseAddress = new Uri("https://lifeos.test/") };

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            var route = request.RequestUri!.AbsolutePath.Split('/')[^1];
            Calls.Add(route);

            if (route == Failing)
            {
                return Problem(HttpStatusCode.ServiceUnavailable,
                    """{"title":"Journal memory unavailable","detail":"Journal memory is unavailable right now. Your journal is safe; try again later.","status":503}""");
            }

            switch (route)
            {
                case "status":
                    return Json(Status(_pending, indexed));

                case "sync":
                    var limit = (await request.Content!.ReadFromJsonAsync<SyncJournalMemoryRequest>())!.Limit!.Value;
                    SyncLimits.Add(limit);
                    OnSync?.Invoke();

                    JournalMemorySyncResponse result;

                    if (_syncCount < SyncResults.Count)
                    {
                        result = SyncResults[_syncCount];
                    }
                    else if (StuckSync is not null)
                    {
                        result = StuckSync;
                    }
                    else
                    {
                        var done = Math.Min(limit, _pending);
                        result = Sync(done, _pending - done);
                    }

                    _syncCount++;
                    _pending = result.PendingRemaining;

                    if (_syncCount > 100)
                    {
                        throw new InvalidOperationException("Sync loop did not stop.");
                    }

                    return Json(result);

                default:
                    PendingAtAsk = _pending;
                    Questions.Add((await request.Content!.ReadFromJsonAsync<AskJournalMemoryRequest>())!.Question!);
                    return Json(Answer);
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return send(request);
        }
    }
}
