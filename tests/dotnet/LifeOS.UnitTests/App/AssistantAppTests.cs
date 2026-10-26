using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LifeOS.App.Services;
using LifeOS.App.Services.Assistant;
using LifeOS.App.Services.Navigation;
using LifeOS.Contracts.Assistant;
using LifeOS.Contracts.Finance.Categories;

namespace LifeOS.UnitTests.App;

// AI-007 F4 app: LifeOS Assistant. The API client (F1/F2 routes and payloads, problem codes, failures), the
// conversation flow against an in-memory API (idempotent send, retry of the failed turn, one draft action at a
// time, reload after conflicts, nothing lost on reopening), the draft editor's PATCH, and the pages, checked by
// source as in the other App tests. No network, provider or database call.
public class AssistantAppTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    // ---- Navigation and registration ----

    [Fact]
    public void Assistant_IsAModuleInMore_NotInTheDock_AndItsPagesSelectMore()
    {
        Assert.Contains("new(\"Assistant\", \"Record expenses and income by writing them\", \"assistant\", \"assistant\")", Source("Pages", "More.razor"));
        Assert.DoesNotContain("assistant", Source("Layout", "BottomDock.razor"), StringComparison.OrdinalIgnoreCase);

        foreach (var path in new[] { "assistant", "assistant/0198c0de-0000-7000-8000-000000000001" })
        {
            Assert.Equal(["More"], DockNavigation.Items.Where(item => item.IsActive(DockNavigation.Normalize(path))).Select(item => item.Label));
        }

        Assert.StartsWith("@page \"/assistant\"", Source("Pages", Path.Combine("Assistant", "Assistant.razor")));
        Assert.StartsWith("@page \"/assistant/{ConversationId:guid}\"", Page());
        Assert.Contains("case \"assistant\":", Source("Shared", "AppIcon.razor"));
    }

    [Fact]
    public void Client_UsesTheAuthorizedPipeline_AndTheAssistantTimeoutOnlyForTurns()
    {
        var program = File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "MauiProgram.cs"));

        Assert.Matches(new Regex(@"new LifeOS\.App\.Services\.Assistant\.AssistantApiClient\(\s*CreateAuthorizedHttpClient\(services\), CreateAuthorizedHttpClient\(services, ApiTimeouts\.Assistant\)\)"), program);
        Assert.Single(Regex.Matches(program, Regex.Escape("ApiTimeouts.Assistant")));
        Assert.True(ApiTimeouts.Assistant > TimeSpan.FromSeconds(60 + 75) && ApiTimeouts.Assistant <= TimeSpan.FromMinutes(4));
    }

    // ---- Client ----

    [Fact]
    public async Task Client_SendsTheMessageWithItsKey_OnTheTurnClient_AndConfirmsTheSeenVersion()
    {
        var server = new FakeAssistantApi();
        var defaultRequests = new List<string>();
        var turnRequests = new List<string>();
        var client = new AssistantApiClient(
            Http(async request => { defaultRequests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}"); return await server.HandleAsync(request); }),
            Http(async request => { turnRequests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}"); return await server.HandleAsync(request); }));
        var key = Guid.CreateVersion7();

        var sent = await client.SendMessageAsync(server.ConversationId, key, "Speso 5€ per colazione con Pamela");
        var confirmed = await client.ConfirmOperationAsync(server.Operation.Id, 2);

        Assert.True(sent.IsSuccess);
        Assert.Equal([$"POST /api/assistant/conversations/{server.ConversationId}/messages"], turnRequests);
        Assert.Equal(key, server.LastSendBody!.ClientMessageId);
        Assert.Equal("Speso 5€ per colazione con Pamela", server.LastSendBody.Text);
        Assert.Equal([$"POST /api/assistant/operations/{server.Operation.Id}/confirm"], defaultRequests);
        Assert.Equal(2, server.LastVersion);
        Assert.False(confirmed.IsSuccess);
        Assert.Equal(AssistantErrorCodes.VersionConflict, confirmed.Code);
        Assert.Equal(server.Operation.DraftVersion, confirmed.CurrentVersion);
    }

    [Fact]
    public async Task Client_FollowsMessagePages_AndReportsProblemsAndTransportFailuresReadably()
    {
        var server = new FakeAssistantApi();
        server.AddMessages(250);
        var client = server.Client();

        var messages = await client.GetMessagesAsync(server.ConversationId);

        Assert.Equal(Enumerable.Range(1, 250), messages.Value!.Select(message => message.Sequence));

        var notFound = await client.GetConversationAsync(Guid.CreateVersion7());
        Assert.Equal(("Questa conversazione non esiste.", AssistantErrorCodes.ConversationNotFound), (notFound.Error, notFound.Code));

        var bare = new AssistantApiClient(Http(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))), Http(_ => throw new HttpRequestException()));
        Assert.Equal(AssistantApiClient.UnavailableMessage, (await bare.GetConversationAsync(Guid.CreateVersion7())).Error);
        Assert.Equal(ApiErrors.UnreachableMessage, (await bare.SendMessageAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), "x")).Error);
    }

    // ---- Flow: messages ----

    [Fact]
    public async Task Send_ShowsTheStoredTurnAndDraft_AndReopeningLosesNothing()
    {
        var server = new FakeAssistantApi();
        var flow = new AssistantChatFlow(server.Client(), server.ConversationId);
        await flow.LoadAsync();

        flow.Text = "  Speso 5€ per colazione con Pamela ";
        await flow.SendAsync();

        Assert.Equal(("", (AssistantPendingMessage?)null, (string?)null), (flow.Text, flow.Pending, flow.SendError));
        Assert.Equal(["Speso 5€ per colazione con Pamela", "Da quale conto?"], flow.Messages.Select(message => message.Content));
        Assert.Equal(server.Operation.Id, flow.Operation!.Id);

        var reopened = new AssistantChatFlow(server.Client(), server.ConversationId);
        await reopened.LoadAsync();

        Assert.Equal(flow.Messages, reopened.Messages);
        Assert.Equal((flow.Operation.Id, flow.Operation.DraftVersion), (reopened.Operation!.Id, reopened.Operation.DraftVersion));
    }

    [Fact]
    public async Task ALostAnswer_KeepsTheMessagePending_AndSendingAgainReusesItsKey_StoringItOnce()
    {
        var server = new FakeAssistantApi { LoseNextSendAnswer = true };
        var flow = new AssistantChatFlow(server.Client(), server.ConversationId);
        await flow.LoadAsync();
        flow.Text = "Speso 12";

        await flow.SendAsync();

        // The API stored it before the answer was lost: the reload shows it, nothing stays pending.
        Assert.Null(flow.Pending);
        Assert.Single(flow.Messages, message => message.Role == "User");

        server.RejectSends = true;
        flow.Text = "Speso 7";
        await flow.SendAsync();
        var pending = flow.Pending!;

        Assert.NotNull(flow.SendError);
        server.RejectSends = false;
        await flow.ResendAsync();
        await flow.ResendAsync();

        Assert.Null(flow.Pending);
        Assert.Equal(2, server.SendKeys.Distinct().Count());
        Assert.Contains(pending.ClientMessageId, server.SendKeys);
        Assert.Equal(["Speso 12", "Speso 7"], flow.Messages.Where(message => message.Role == "User").Select(message => message.Content));
    }

    [Fact]
    public async Task AFailedTurn_OffersRetryOnlyOnTheLastMessage_AndRetriesTheSameMessage()
    {
        var server = new FakeAssistantApi { NextTurnFails = "assistant_timeout" };
        var flow = new AssistantChatFlow(server.Client(), server.ConversationId);
        await flow.LoadAsync();
        flow.Text = "Speso 5";

        await flow.SendAsync();

        var failed = Assert.Single(flow.Messages);
        Assert.Equal(failed, flow.RetryableMessage);
        Assert.Equal("The assistant took too long to answer.", AssistantDisplay.FailureText(failed.Turn!.FailureCode));

        await flow.RetryAsync();

        Assert.Equal([failed.Id], server.Retried);
        Assert.Equal(AssistantTurnStatuses.Completed, flow.Messages[0].Turn!.Status);
        Assert.Null(flow.RetryableMessage);
    }

    [Fact]
    public void Validation_RefusesEmptyAndTooLongMessages()
    {
        Assert.Equal(AssistantChatFlow.TextRequiredMessage, AssistantChatFlow.Validate("   "));
        Assert.NotNull(AssistantChatFlow.Validate(new string('a', AssistantChatFlow.MaxMessageLength + 1)));
        Assert.Null(AssistantChatFlow.Validate(new string('a', AssistantChatFlow.MaxMessageLength)));
    }

    // ---- Flow: draft actions ----

    [Fact]
    public async Task Confirm_IsSentOnce_EvenWhenTappedTwice_AndShowsTheRecordedDraft()
    {
        var server = new FakeAssistantApi { Operation = Draft(canConfirm: true) };
        server.Gate = new TaskCompletionSource();
        var flow = new AssistantChatFlow(server.Client(), server.ConversationId);
        await flow.LoadAsync();

        var first = flow.ConfirmAsync();
        var second = flow.ConfirmAsync();
        Assert.True(flow.IsOperationBusy);
        server.Gate.SetResult();

        Assert.Equal((true, false), (await first, await second));
        Assert.Equal(1, server.Confirmations);
        Assert.Equal((AssistantOperationStatuses.Executed, false), (flow.Operation!.Status, flow.Operation.CanConfirm));
        Assert.Equal(AssistantChatFlow.RecordedNotice, flow.OperationNotice);
        Assert.False(await flow.ConfirmAsync());
        Assert.Equal(1, server.Confirmations);
    }

    [Fact]
    public async Task Confirm_IsNotSent_WhileTheApiSaysTheDraftCannotBeConfirmed()
    {
        var server = new FakeAssistantApi();
        var flow = new AssistantChatFlow(server.Client(), server.ConversationId);
        await flow.LoadAsync();

        Assert.False(await flow.ConfirmAsync());
        Assert.Equal(0, server.Confirmations);
    }

    [Fact]
    public async Task AVersionConflict_ShowsTheApiMessage_AndReloadsTheStoredDraft()
    {
        var server = new FakeAssistantApi { Operation = Draft(canConfirm: true) };
        var flow = new AssistantChatFlow(server.Client(), server.ConversationId);
        await flow.LoadAsync();
        server.Operation = server.Operation with { DraftVersion = 4, Draft = server.Operation.Draft with { Amount = 9m } };

        Assert.False(await flow.ConfirmAsync());

        Assert.Equal("La bozza è cambiata nel frattempo.", flow.OperationError);
        Assert.Equal((4, 9m), (flow.Operation!.DraftVersion, flow.Operation.Draft.Amount));
        Assert.Equal(0, server.Confirmations);
    }

    [Fact]
    public async Task Cancel_EndsTheDraft_AndNothingIsRecorded()
    {
        var server = new FakeAssistantApi();
        var flow = new AssistantChatFlow(server.Client(), server.ConversationId);
        await flow.LoadAsync();

        Assert.True(await flow.CancelAsync());

        Assert.Equal(AssistantOperationStatuses.Cancelled, flow.Operation!.Status);
        Assert.Equal(AssistantChatFlow.CancelledNotice, flow.OperationNotice);
        Assert.Equal(0, server.Confirmations);
        Assert.False(await flow.CancelAsync());
    }

    // ---- Draft editor ----

    [Fact]
    public void Editor_SendsOnlyTheChangedFields_AndClearsEmptiedOnes()
    {
        var account = Guid.CreateVersion7();
        var operation = Draft(amount: 5m, accountId: null, note: "colazione con Pamela", occurredTime: "08:30:00");
        var editor = AssistantDraftEditor.From(operation);

        Assert.Equal(((UpdateAssistantOperationRequest?)null, (string?)null), editor.BuildPatch(operation.DraftVersion));

        editor.AmountText = "5,50";
        editor.AccountId = account.ToString();
        editor.Note = "";
        editor.OccurredTime = "08:30";

        var (request, error) = editor.BuildPatch(operation.DraftVersion);

        Assert.Null(error);
        Assert.Equal((operation.DraftVersion, 5.50m, account), (request!.ExpectedVersion, request.Amount, request.AccountId));
        Assert.Equal(new[] { "note" }, request.Clear);
        Assert.Null(request.Type);
        Assert.Null(request.CategoryId);
        Assert.Null(request.Currency);
        Assert.Null(request.OccurredTime);
    }

    [Theory]
    [InlineData("0", AssistantDraftEditor.AmountInvalidMessage)]
    [InlineData("-3", AssistantDraftEditor.AmountInvalidMessage)]
    [InlineData("dodici", AssistantDraftEditor.AmountInvalidMessage)]
    public void Editor_RefusesAnInvalidAmount(string amount, string message)
    {
        var operation = Draft();
        var editor = AssistantDraftEditor.From(operation);
        editor.AmountText = amount;

        Assert.Equal(((UpdateAssistantOperationRequest?)null, message), editor.BuildPatch(operation.DraftVersion));
    }

    [Fact]
    public void Editor_OffersOnlyTheUsersCategoriesOfTheType_AndDropsAnIncompatibleOneOnATypeChange()
    {
        var food = Category("Cibo", "Expense");
        var bar = Category("Bar", "Expense", food.Id);
        var salary = Category("Stipendio", "Income");
        CategoryResponse[] categories = [food, bar, salary];
        var operation = Draft(categoryId: bar.Id);
        var editor = AssistantDraftEditor.From(operation);

        var options = editor.CategoryOptions(categories);
        Assert.Equal([food.Id], options.TopLevel.Select(node => node.Category.Id));
        Assert.Equal([bar.Id], options.TopLevel[0].Children.Select(child => child.Id));

        editor.ChangeType(AssistantDraftEditor.IncomeType, categories);

        Assert.Equal("", editor.CategoryId);
        Assert.Equal([salary.Id], editor.CategoryOptions(categories).TopLevel.Select(node => node.Category.Id));
        var (request, _) = editor.BuildPatch(operation.DraftVersion);
        Assert.Equal("Income", request!.Type);
        Assert.Equal(new[] { "categoryId" }, request.Clear);
    }

    [Fact]
    public void Display_MarksASuggestedCategory_AndNamesWhatIsMissing()
    {
        Assert.Equal("Cibo (suggested)", AssistantDisplay.CategoryText(new AssistantDraftCategoryResponse(Guid.CreateVersion7(), "Cibo", "Expense", null, "Suggested")));
        Assert.Equal("Cibo", AssistantDisplay.CategoryText(new AssistantDraftCategoryResponse(Guid.CreateVersion7(), "Cibo", "Expense", null, "User")));
        Assert.Equal("Missing: account, category.", AssistantDisplay.MissingText(["accountId", "categoryId"]));
        Assert.Equal("The assistant is unavailable right now.", AssistantDisplay.FailureText(AssistantFailureCodes.AssistantUnavailable));
    }

    // ---- Pages ----

    [Fact]
    public void ConversationPage_HasHistoryComposerProcessingErrorsAndRetry()
    {
        var page = Page();

        Assert.Contains("@foreach (var message in flow.Messages)", page);
        Assert.Contains("@bind=\"flow.Text\"", page);
        Assert.Contains("The assistant is working on it...", page);
        Assert.Contains("AssistantDisplay.FailureText(failed.FailureCode)", page);
        Assert.Contains("@onclick=\"RetryAsync\" disabled=\"@flow.IsSending\">Retry</button>", page);
        Assert.Contains("@onclick=\"ResendAsync\">Send again</button>", page);
        Assert.Contains("<AssistantDraftCard Flow=\"flow\" Accounts=\"accounts\" Categories=\"categories\"", page);
        Assert.Contains("AccountsApi.GetAccountsAsync", page);
        Assert.Contains("CategoriesApi.GetCategoriesAsync", page);
        Assert.Contains("white-space: pre-wrap", Source("Pages", Path.Combine("Assistant", "AssistantConversation.razor.css")));
    }

    [Fact]
    public void DraftCard_ConfirmsAndCancelsOnlyOnItsButtons_DisabledWhileBusy()
    {
        var card = Source("Assistant", "AssistantDraftCard.razor");

        Assert.Contains("@onclick=\"ConfirmAsync\" disabled=\"@(busy || !operation.CanConfirm)\"", card);
        Assert.Contains("@onclick=\"CancelAsync\" disabled=\"@busy\">Cancel</button>", card);
        Assert.Contains("<select id=\"assistant-account\"", card);
        Assert.Contains("@foreach (var account in Accounts)", card);
        Assert.Contains("<option value=\"@child.Id\">↳ @child.Name</option>", card);
        Assert.Contains("type=\"date\"", card);
        Assert.Contains("type=\"time\"", card);
        Assert.Contains("operation.MissingFields", card);
        Assert.Contains("operation.Issues", card);
    }

    [Fact]
    public void TheAssistant_IsFinanceOnly_AndKeepsNothingOnTheDevice()
    {
        foreach (var source in new[] { Page(), Source("Pages", Path.Combine("Assistant", "Assistant.razor")), Source("Assistant", "AssistantDraftCard.razor"),
            AppSource("AssistantChatFlow.cs"), AppSource("AssistantApiClient.cs"), AppSource("AssistantDraftEditor.cs") })
        {
            Assert.DoesNotMatch(new Regex(@"Preferences|SecureStorage|localStorage|sessionStorage|File\.Write|ILogger|Console\.|Debug\.Write|Trace\."), source);
            Assert.DoesNotMatch(new Regex(@"Nutrition|Hydration|Habit|Journal|openai|gpt", RegexOptions.IgnoreCase), source);
        }
    }

    // ---- Helpers ----

    private static AssistantOperationResponse Draft(bool canConfirm = false, decimal? amount = 5m, Guid? accountId = null, Guid? categoryId = null,
        string? note = "colazione", string? occurredTime = null) =>
        new(Guid.CreateVersion7(), Guid.Empty, AssistantOperationKindNames.FinanceTransactionCreate,
            canConfirm ? AssistantOperationStatuses.AwaitingConfirmation : AssistantOperationStatuses.Collecting, 3, 1,
            new FinanceTransactionDraftResponse("Expense", amount, "EUR", null,
                accountId is { } account ? new AssistantDraftAccountResponse(account, "Revolut", "EUR", "BankAccount") : null,
                categoryId is { } category ? new AssistantDraftCategoryResponse(category, "Bar", "Expense", null, "Suggested") : null,
                note, null, occurredTime, null),
            canConfirm ? [] : ["accountId"], [], canConfirm, Now.AddDays(1), null, Now, Now, null);

    private static CategoryResponse Category(string name, string type, Guid? parent = null) => new(Guid.CreateVersion7(), name, type, parent, Now);

    private static string Page() => Source("Pages", Path.Combine("Assistant", "AssistantConversation.razor"));

    private static string AppSource(string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "Services", "Assistant", fileName));

    private static HttpClient Http(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send)) { BaseAddress = new Uri("https://lifeos.test/") };

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));

    // The F1/F2 API in memory for one conversation: send stores a user message once per client key and answers
    // with a template reply (or a failed turn); retry completes the turn; confirm/cancel check the version.
    private sealed class FakeAssistantApi
    {
        private readonly List<AssistantMessageResponse> _messages = [];

        public Guid ConversationId { get; } = Guid.CreateVersion7();

        public AssistantOperationResponse Operation { get; set; } = Draft();

        public bool LoseNextSendAnswer { get; set; }

        public bool RejectSends { get; set; }

        public string? NextTurnFails { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public SendAssistantMessageRequest? LastSendBody { get; private set; }

        public int? LastVersion { get; private set; }

        public List<Guid> SendKeys { get; } = [];

        public List<Guid> Retried { get; } = [];

        public int Confirmations { get; private set; }

        public AssistantApiClient Client() => new(Http(HandleAsync), Http(HandleAsync));

        public void AddMessages(int count)
        {
            for (var index = 0; index < count; index++)
            {
                _messages.Add(Message("User", $"m{index}", Guid.CreateVersion7(), null, null));
            }
        }

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var conversation = new AssistantConversationResponse(ConversationId, Guid.Empty, Now, Now);
            var operationPath = $"/api/assistant/operations/{Operation.Id}";
            var open = Operation.Status is AssistantOperationStatuses.Collecting or AssistantOperationStatuses.AwaitingConfirmation;

            switch (request.Method.Method, path)
            {
                case ("GET", var p) when p == $"/api/assistant/conversations/{ConversationId}":
                    return Json(new AssistantConversationDetailResponse(conversation, open ? Operation : null));

                case ("GET", var p) when p == $"/api/assistant/conversations/{ConversationId}/messages":
                {
                    var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                    var after = int.Parse(query["afterSequence"]!);
                    var limit = int.Parse(query["limit"]!);
                    var items = _messages.Where(message => message.Sequence > after).Take(limit).ToList();
                    return Json(new AssistantMessagePageResponse(conversation, items, items.Count == limit ? items[^1].Sequence : null));
                }

                case ("GET", var p) when p == operationPath:
                    return Json(Operation);

                case ("POST", var p) when p == $"/api/assistant/conversations/{ConversationId}/messages":
                {
                    var body = (await request.Content!.ReadFromJsonAsync<SendAssistantMessageRequest>())!;
                    LastSendBody = body;
                    SendKeys.Add(body.ClientMessageId!.Value);

                    if (RejectSends)
                    {
                        throw new HttpRequestException("offline");
                    }

                    var existing = _messages.FirstOrDefault(message => message.ClientMessageId == body.ClientMessageId);
                    if (existing is null)
                    {
                        var failure = NextTurnFails;
                        NextTurnFails = null;
                        existing = Message("User", body.Text!, body.ClientMessageId, null,
                            failure is null ? new AssistantTurnStateResponse(AssistantTurnStatuses.Completed, 1, null) : new AssistantTurnStateResponse(AssistantTurnStatuses.Failed, 1, failure));
                        _messages.Add(existing);

                        if (failure is null)
                        {
                            _messages.Add(Message("Assistant", "Da quale conto?", null, existing.Id, null));
                        }
                    }

                    if (LoseNextSendAnswer)
                    {
                        LoseNextSendAnswer = false;
                        throw new HttpRequestException("answer lost");
                    }

                    return Json(new AssistantTurnResponse(existing, null, false), HttpStatusCode.Created);
                }

                case ("POST", var p) when p.StartsWith("/api/assistant/messages/", StringComparison.Ordinal) && p.EndsWith("/retry", StringComparison.Ordinal):
                {
                    var id = Guid.Parse(p.Split('/')[4]);
                    Retried.Add(id);
                    var index = _messages.FindIndex(message => message.Id == id);
                    _messages[index] = _messages[index] with { Turn = new AssistantTurnStateResponse(AssistantTurnStatuses.Completed, 2, null) };
                    _messages.Add(Message("Assistant", "Da quale conto?", null, id, null));
                    return Json(new AssistantTurnResponse(_messages[index], null, false));
                }

                case ("POST", var p) when p == operationPath + "/confirm":
                {
                    LastVersion = (await request.Content!.ReadFromJsonAsync<AssistantOperationVersionRequest>())!.ExpectedVersion;

                    if (Gate is not null)
                    {
                        await Gate.Task;
                    }

                    if (LastVersion != Operation.DraftVersion)
                    {
                        return Problem(HttpStatusCode.Conflict,
                            $$"""{"title":"Bozza cambiata.","detail":"La bozza è cambiata nel frattempo.","code":"version_conflict","currentVersion":{{Operation.DraftVersion}}}""");
                    }

                    Confirmations++;
                    var transaction = Guid.CreateVersion7();
                    Operation = Operation with { Status = AssistantOperationStatuses.Executed, CanConfirm = false, TransactionId = transaction };
                    return Json(new AssistantOperationConfirmationResponse(transaction, false, Operation));
                }

                case ("POST", var p) when p == operationPath + "/cancel":
                    Operation = Operation with { Status = AssistantOperationStatuses.Cancelled, CanConfirm = false };
                    return Json(Operation);

                default:
                    return Problem(HttpStatusCode.NotFound,
                        """{"title":"Conversazione non trovata.","detail":"Questa conversazione non esiste.","code":"conversation_not_found"}""");
            }
        }

        private AssistantMessageResponse Message(string role, string content, Guid? clientMessageId, Guid? replyTo, AssistantTurnStateResponse? turn) =>
            new(Guid.CreateVersion7(), ConversationId, _messages.Count + 1, role, content, clientMessageId, replyTo, turn, Now);

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

        private static HttpResponseMessage Problem(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/problem+json") };
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
