using System.Net;
using System.Text;
using System.Text.Json;
using LifeOS.Api.Assistant;
using LifeOS.Application.Assistant;
using LifeOS.Infrastructure;
using LifeOS.Infrastructure.Interpretation;
using LifeOS.Infrastructure.Nutrition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeOS.UnitTests.Assistant;

// AI-007 F3: the .NET side of the interpretation boundary, with a scripted HTTP handler (no network): the
// exact minimised payload, the strict parsing of the answer, the mapping of every service outcome, and the
// default configuration that never builds a client at all.
public class AssistantInterpreterClientTests
{
    // Synthetic, test-only service key (never a real secret).
    private const string ServiceKey = "test-service-key-0123456789abcdefghijklmnop";

    private static readonly AssistantInterpretationRequest Request = new(2, "Ho speso 5 euro per colazione con Pamela",
        new AssistantInterpretationContext("it-IT",
            new AssistantOpenDraftContext("finance.transaction.create", "expense", ["type", "amount"], ["account"], "account"),
            new AssistantFinanceContext(["Ristoranti"], [])));

    private const string Answer =
        """
        {"output_version": 2, "provider": "fake", "model": "fake-model", "prompt_version": "assistant-interpret-v3",
         "interpretation": {"protocol_version": 2, "intent": "finance_expense", "ambiguous_fields": [], "unsupported_reason": null,
          "hydration": {
            "amount": {"state": "absent", "value": null, "span": null},
            "unit": {"state": "absent", "value": null, "span": null},
            "date": {"state": "absent", "kind": null, "days_ago": null, "weekday": null, "day": null, "month": null, "year": null, "span": null},
            "time": {"state": "absent", "hour": null, "minute": null, "span": null}},
          "meal": {
            "description": {"state": "absent", "value": null, "span": null},
            "meal_type": {"state": "absent", "value": null, "span": null},
            "date": {"state": "absent", "kind": null, "days_ago": null, "weekday": null, "day": null, "month": null, "year": null, "span": null},
            "time": {"state": "absent", "hour": null, "minute": null, "span": null}},
          "finance": {
            "type": {"state": "absent", "value": null, "span": null},
            "amount": {"state": "stated", "value": "5", "span": "5 euro"},
            "currency": {"state": "stated", "value": "EUR", "span": "euro"},
            "account": {"state": "absent", "value": null, "span": null},
            "category": {"state": "inferred", "value": "Ristoranti", "span": null},
            "note": {"state": "stated", "value": "colazione con Pamela", "span": "colazione con Pamela"},
            "date": {"state": "stated", "kind": "weekday", "days_ago": null, "weekday": "monday", "day": null, "month": null, "year": null, "span": "lunedì"},
            "time": {"state": "absent", "hour": null, "minute": null, "span": null}}}}
        """;

    [Fact]
    public async Task SendsOnlyTheMinimisedRequest_InSnakeCase_ToTheInterpretPath()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Answer));

        await Client(handler).InterpretAsync(Request, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(("http://127.0.0.1:8000/v1/assistant/interpret", $"Bearer {ServiceKey}"), (request.Uri, request.Authorization));
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(["protocol_version", "message", "context"], root.EnumerateObject().Select(property => property.Name));
        var context = root.GetProperty("context");
        Assert.Equal(["locale", "open_draft", "finance"], context.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["expense_categories", "income_categories"], context.GetProperty("finance").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["kind", "type", "filled_fields", "missing_fields", "awaiting_field"],
            context.GetProperty("open_draft").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task AValidAnswer_IsParsedStrictly_IntoTheProtocol()
    {
        var result = await Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Answer))).InterpretAsync(Request, CancellationToken.None);

        var interpretation = result.Interpretation!;
        Assert.Equal((AssistantIntent.FinanceExpense, 2), (interpretation.Intent, interpretation.ProtocolVersion));
        Assert.Equal(AssistantHydrationFields.None, interpretation.Hydration);
        Assert.Equal(AssistantMealFields.None, interpretation.Meal);
        Assert.Equal(new AssistantTextField(AssistantFieldState.Stated, "5", "5 euro"), interpretation.Finance!.Amount);
        Assert.Equal(new AssistantTextField(AssistantFieldState.Inferred, "Ristoranti"), interpretation.Finance.Category);
        Assert.Equal((AssistantDateKind.Weekday, DayOfWeek.Monday), (interpretation.Finance.Date.Kind!.Value, interpretation.Finance.Date.Weekday!.Value));
    }

    public static TheoryData<string> UnreadableAnswers => new()
    {
        // An unknown field, an unknown enum value, a missing interpretation, another output version (v1 included),
        // a missing section (a v1 answer), not JSON.
        Answer.Replace("\"unsupported_reason\": null", "\"unsupported_reason\": null, \"confidence\": 0.9"),
        Answer.Replace("finance_expense", "finance_transfer"),
        """{"output_version": 2, "provider": "fake", "model": "m", "prompt_version": "p", "interpretation": null}""",
        Answer.Replace("\"output_version\": 2", "\"output_version\": 1"),
        Answer.Replace("\"output_version\": 2", "\"output_version\": 3"),
        Answer[..Answer.IndexOf("\"hydration\"", StringComparison.Ordinal)] + Answer[Answer.IndexOf("\"finance\"", StringComparison.Ordinal)..],
        "not json"
    };

    [Theory]
    [MemberData(nameof(UnreadableAnswers))]
    public async Task AnUnreadableAnswer_IsInvalidOutput(string body)
    {
        var result = await Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))).InterpretAsync(Request, CancellationToken.None);

        Assert.Equal(AssistantInterpretationFailure.InvalidOutput, result.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, """{"error": {"code": "interpretation_refused"}}""", AssistantInterpretationFailure.Refused)]
    [InlineData(HttpStatusCode.BadGateway, """{"error": {"code": "interpretation_failed"}}""", AssistantInterpretationFailure.InvalidOutput)]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"error": {"code": "invalid_request"}}""", AssistantInterpretationFailure.InvalidOutput)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error": {"code": "provider_unavailable"}}""", AssistantInterpretationFailure.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error": {"code": "unauthorized"}}""", AssistantInterpretationFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "oops", AssistantInterpretationFailure.Unavailable)]
    public async Task EveryServiceFailure_IsAStableResult(HttpStatusCode status, string body, AssistantInterpretationFailure expected)
    {
        var result = await Client(new ScriptedHandler(_ => Json(status, body))).InterpretAsync(Request, CancellationToken.None);

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task ATransportErrorIsUnavailable_AndTheClientTimeoutIsTimeout()
    {
        var unreachable = await Client(new ScriptedHandler(_ => throw new HttpRequestException("refused")))
            .InterpretAsync(Request, CancellationToken.None);
        var timedOut = await Client(new ScriptedHandler(_ => throw new TaskCanceledException("timeout")))
            .InterpretAsync(Request, CancellationToken.None);

        Assert.Equal((AssistantInterpretationFailure.Unavailable, AssistantInterpretationFailure.Timeout), (unreachable.Failure!.Value, timedOut.Failure!.Value));
    }

    [Fact]
    public async Task TheCallersOwnCancellation_IsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Answer))).InterpretAsync(Request, cancelled.Token));
    }

    // ---- Configuration: disabled by default ----

    [Fact]
    public void ByDefault_TheInterpreterIsDisabled_EvenWithTheAiServiceConfigured()
    {
        var aiService = new NutritionAiOptions(new Uri("http://127.0.0.1:8000/"), TimeSpan.FromSeconds(60), ServiceKey);

        var options = AssistantAiConfiguration.Read(Configuration(), aiService);
        var interpreter = Interpreter(aiService, options);

        Assert.False(options.Enabled);
        Assert.IsType<DisabledAssistantInterpreter>(interpreter);
    }

    [Fact]
    public async Task TheDisabledInterpreter_AnswersUnavailable_WithoutAnyNetworkCall()
    {
        var result = await new DisabledAssistantInterpreter().InterpretAsync(Request, CancellationToken.None);

        Assert.Equal(AssistantInterpretationFailure.Unavailable, result.Failure);
    }

    [Fact]
    public void Enabled_UsesTheAiService_WithTheAssistantsTimeout()
    {
        var aiService = new NutritionAiOptions(new Uri("http://127.0.0.1:8000/"), TimeSpan.FromSeconds(60), ServiceKey);

        var options = AssistantAiConfiguration.Read(Configuration(("AssistantAi:Enabled", "true"), ("AssistantAi:TimeoutSeconds", "30")), aiService);

        Assert.Equal((true, TimeSpan.FromSeconds(30)), (options.Enabled, options.Timeout));
        Assert.IsType<AssistantInterpreterClient>(Interpreter(aiService, options));
    }

    [Theory]
    [InlineData("AssistantAi:Enabled", "yes")]
    [InlineData("AssistantAi:TimeoutSeconds", "0")]
    [InlineData("AssistantAi:TimeoutSeconds", "76")]
    public void AnInvalidSetting_StopsTheApiAtStartup(string key, string value)
    {
        var aiService = new NutritionAiOptions(new Uri("http://127.0.0.1:8000/"), TimeSpan.FromSeconds(60), ServiceKey);

        var failure = Assert.Throws<InvalidOperationException>(() => AssistantAiConfiguration.Read(Configuration((key, value)), aiService));

        Assert.Contains(key, failure.Message);
        Assert.DoesNotContain(value + ".", failure.Message);
    }

    [Fact]
    public void EnablingWithoutTheAiService_StopsTheApiAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AssistantAiConfiguration.Read(Configuration(("AssistantAi:Enabled", "true")), NutritionAiOptions.Disabled));
    }

    private static IAssistantInterpreter Interpreter(NutritionAiOptions aiService, AssistantAiOptions assistant)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure("Host=localhost;Database=unused", aiService, assistantAi: assistant);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAssistantInterpreter>();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value))).Build();

    private static AssistantInterpreterClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") }, ServiceKey, NullLogger<AssistantInterpreterClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(string Uri, string? Authorization, string Body);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
