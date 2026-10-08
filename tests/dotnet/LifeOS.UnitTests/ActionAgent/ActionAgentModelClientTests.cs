using System.Net;
using System.Text;
using System.Text.Json;
using LifeOS.Application.ActionAgent;
using LifeOS.Infrastructure.ActionAgent;
using LifeOS.UnitTests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeOS.UnitTests.ActionAgent;

// AI-002: the .NET side of the agent-step boundary, with a scripted HTTP handler: the exact payload sent
// (tool definitions, executed steps, framing; no identifiers), the three decision shapes, and how every
// service outcome maps to an application result. No network.
public class ActionAgentModelClientTests
{
    // Synthetic, test-only service key (never a real secret).
    private const string ServiceKey = "test-service-key-0123456789abcdefghijklmnop";

    private static readonly AgentStepRequest Request = new(
        ActionAgentTools.ToolSchemaVersion,
        new AgentTaskContext("2026-10", ["2026-10", "2026-11"]),
        ActionAgentTools.Definitions,
        [new AgentToolStep(ActionAgentTools.GetBudgetStatus, FakeActionAgentModel.Json("""{"year":2026,"month":10,"currency":"EUR"}"""),
            FakeActionAgentModel.Json("""{"year":2026,"month":10,"currency":"EUR","budget_set":true,"amount":400}"""))],
        5);

    private const string Identity = """ "output_version": 1, "provider": "groq", "model": "openai/gpt-oss-20b", "prompt_version": "action-agent-v1" """;

    [Fact]
    public async Task SendsTheToolsStepsAndFraming_ToTheStepPath()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Decision("""{"type":"no_action","reason":"Fine."}""")));

        await Client(handler).NextStepAsync(Request, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "http://127.0.0.1:8000/v1/action-agent/step"), (request.Method, request.Uri));
        Assert.Equal([$"Bearer {ServiceKey}"], request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(["tool_schema_version", "context", "tools", "steps", "max_steps"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["get_weekly_review", "get_budget_status"], root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        Assert.Equal("object", root.GetProperty("tools")[1].GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal(["current_month", "target_months"], root.GetProperty("context").EnumerateObject().Select(property => property.Name));
        var step = root.GetProperty("steps")[0];
        Assert.Equal(["tool", "arguments", "result"], step.EnumerateObject().Select(property => property.Name));
        Assert.Equal(400, step.GetProperty("result").GetProperty("amount").GetInt32());
        Assert.Equal(5, root.GetProperty("max_steps").GetInt32());
        Assert.DoesNotContain("user", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("review_id", request.Body);
    }

    [Fact]
    public async Task TheThreeDecisionShapes_AreRead()
    {
        var call = await Step(Decision("""{"type":"call_tool","tool":"get_budget_status","arguments":{"year":2026,"month":10,"currency":"EUR"}}"""));
        var proposal = await Step(Decision(
            """{"type":"propose_budget_adjustment","proposal":{"year":2026,"month":10,"currency":"EUR","proposed_amount":500.5,"rationale":"Why."}}"""));
        var none = await Step(Decision("""{"type":"no_action","reason":"Fine."}"""));

        Assert.Equal((AgentDecisionKind.CallTool, "get_budget_status"), (call.Decision!.Kind, call.Decision.Tool));
        Assert.Equal(2026, call.Decision.Arguments!.Value.GetProperty("year").GetInt32());
        Assert.Equal(new AgentBudgetProposal(2026, 10, "EUR", 500.5m, "Why."), proposal.Decision!.Proposal);
        Assert.Equal((AgentDecisionKind.NoAction, "Fine."), (none.Decision!.Kind, none.Decision.Reason));
        Assert.Equal(new AgentModelIdentity("groq", "openai/gpt-oss-20b", "action-agent-v1"), none.Identity);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentStepFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, AgentStepFailure.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, AgentStepFailure.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, AgentStepFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, AgentStepFailure.InvalidOutput)]
    [InlineData(HttpStatusCode.UnprocessableEntity, AgentStepFailure.InvalidOutput)]
    public async Task ServiceErrors_MapToApplicationFailures(HttpStatusCode status, AgentStepFailure expected)
    {
        var result = await Client(new ScriptedHandler(_ => Json(status, """{"error":{"code":"x"}}"""))).NextStepAsync(Request, CancellationToken.None);

        Assert.Equal(AgentStepResult.Failed(expected), result);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"output_version": 2, "provider": "groq", "model": "m", "prompt_version": "p", "decision": {"type":"no_action","reason":"x"}}""")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p"}""")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p", "decision": {"type":"execute","tool":"x"}}""")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p", "decision": {"type":"call_tool","tool":"x","arguments":[1]}}""")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p", "decision": {"type":"call_tool","arguments":{}}}""")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p", "decision": {"type":"propose_budget_adjustment","proposal":{"year":2026}}}""")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p", "decision": {"type":"propose_budget_adjustment","proposal":{"year":2026,"month":10,"currency":"EUR","proposed_amount":"500","rationale":"x"}}}""")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p", "decision": {"type":"no_action"}}""")]
    public async Task MalformedAnswers_AreInvalidOutput(string body)
    {
        var result = await Step(body);

        Assert.Equal(AgentStepResult.Failed(AgentStepFailure.InvalidOutput), result);
    }

    [Fact]
    public async Task UnreachableOrTimedOut_IsUnavailable_AndCallerCancellationPropagates()
    {
        var refused = await Client(new ScriptedHandler(_ => throw new HttpRequestException("refused"))).NextStepAsync(Request, CancellationToken.None);
        var timedOut = await Client(new ScriptedHandler(_ => throw new TaskCanceledException("timeout"))).NextStepAsync(Request, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.Equal(AgentStepFailure.Unavailable, refused.Failure);
        Assert.Equal(AgentStepFailure.Unavailable, timedOut.Failure);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}"))).NextStepAsync(Request, cancellation.Token));
    }

    [Fact]
    public async Task WithoutAConfiguredService_EveryStepIsUnavailable_WithoutNetwork()
    {
        var client = new ActionAgentModelClient(null, null, NullLogger<ActionAgentModelClient>.Instance);

        Assert.Equal(AgentStepFailure.Unavailable, (await client.NextStepAsync(Request, CancellationToken.None)).Failure);
        using var httpClient = new HttpClient();
        Assert.Throws<ArgumentException>(() => new ActionAgentModelClient(httpClient, " ", NullLogger<ActionAgentModelClient>.Instance));
    }

    [Fact]
    public async Task Logs_IdentityAndLatency_ButNeverResultsDecisionsOrTheKey()
    {
        var logger = new RecordingLogger();
        await Logged(logger, Json(HttpStatusCode.OK, Decision(
            """{"type":"propose_budget_adjustment","proposal":{"year":2026,"month":10,"currency":"EUR","proposed_amount":512.5,"rationale":"Secret rationale."}}""")))
            .NextStepAsync(Request, CancellationToken.None);
        await Logged(logger, Json(HttpStatusCode.Unauthorized, "{}")).NextStepAsync(Request, CancellationToken.None);

        Assert.Contains(logger.Messages, message => message.Contains("action-agent-v1") && message.Contains("propose_budget_adjustment") && message.Contains(" ms"));
        Assert.Contains(logger.Messages, message => message.Contains("HTTP 401"));
        foreach (var secret in new[] { ServiceKey, "512.5", "Secret rationale", "400", "EUR" })
        {
            Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        }
    }

    private static string Decision(string decision) => $$"""{{{Identity}}, "decision": {{decision}}}""";

    private static Task<AgentStepResult> Step(string body) =>
        Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))).NextStepAsync(Request, CancellationToken.None);

    private static ActionAgentModelClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") }, ServiceKey, NullLogger<ActionAgentModelClient>.Instance);

    private static ActionAgentModelClient Logged(RecordingLogger logger, HttpResponseMessage response) =>
        new(new HttpClient(new ScriptedHandler(_ => response)) { BaseAddress = new Uri("http://127.0.0.1:8000/") }, ServiceKey, logger);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string[] Authorization, string Body);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.ToArray() : [],
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed class RecordingLogger : ILogger<ActionAgentModelClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
