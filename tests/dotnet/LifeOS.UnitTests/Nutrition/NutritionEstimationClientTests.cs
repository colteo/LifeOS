using System.Net;
using System.Text;
using System.Text.Json;
using LifeOS.Api.Nutrition;
using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Infrastructure.Nutrition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeOS.UnitTests.Nutrition;

// The .NET side of the Python AI service boundary (ADR-011), with a scripted HTTP handler: the exact
// payload sent, and how every service outcome maps to an application result. No network.
public class NutritionEstimationClientTests
{
    private static readonly MealEstimationInput Lunch = new("Pollo con le patate", MealType.Lunch);

    // Synthetic, test-only service key (never a real secret).
    private const string ServiceKey = "test-service-key-0123456789abcdefghijklmnop";

    private const string Estimate =
        """{"calories_kcal": 620.0, "protein_grams": 52.5, "carbs_grams": 58, "fat_grams": 20, "assumptions": ["about 180 g chicken"]}""";

    [Fact]
    public async Task SendsOnlyTheDescriptionAndMealType_ToTheEstimatePath()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Estimate));

        await Client(handler).EstimateAsync(Lunch, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:8000/v1/nutrition/estimate-meal", request.Uri);
        Assert.Equal([$"Bearer {ServiceKey}"], request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(["description", "meal_type"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Pollo con le patate", body.RootElement.GetProperty("description").GetString());
        Assert.Equal("Lunch", body.RootElement.GetProperty("meal_type").GetString());
    }

    [Fact]
    public async Task AMealWithoutType_SendsNull()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Estimate));

        await Client(handler).EstimateAsync(new MealEstimationInput("Caffè", null), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal("Caffè", body.RootElement.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("meal_type").ValueKind);
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task AnEstimate_IsReturnedAsDecimals()
    {
        var result = await Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Estimate))).EstimateAsync(Lunch, CancellationToken.None);

        Assert.Equal(new NutritionEstimate(620.0m, 52.5m, 58m, 20m, ["about 180 g chicken"]) with { Assumptions = result.Estimate!.Assumptions },
            result.Estimate);
        Assert.Equal(["about 180 g chicken"], result.Estimate.Assumptions);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, NutritionEstimationFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, NutritionEstimationFailure.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, NutritionEstimationFailure.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, NutritionEstimationFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, NutritionEstimationFailure.NotEstimable)]
    [InlineData(HttpStatusCode.UnprocessableEntity, NutritionEstimationFailure.NotEstimable)]
    public async Task ServiceErrors_MapToApplicationFailures(HttpStatusCode status, NutritionEstimationFailure expected)
    {
        var result = await Client(new ScriptedHandler(_ => Json(status, """{"error": {"code": "x"}}"""))).EstimateAsync(Lunch, CancellationToken.None);

        Assert.Equal(NutritionEstimationResult.Failed(expected), result);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"calories_kcal": 620}""")]
    [InlineData("""{"calories_kcal": "620", "protein_grams": 1, "carbs_grams": 1, "fat_grams": 1, "assumptions": []}""")]
    [InlineData("null")]
    public async Task MalformedEstimates_AreNotEstimable(string body)
    {
        var result = await Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))).EstimateAsync(Lunch, CancellationToken.None);

        Assert.Equal(NutritionEstimationResult.Failed(NutritionEstimationFailure.NotEstimable), result);
    }

    [Fact]
    public async Task UnreachableOrTimedOut_IsUnavailable()
    {
        var refused = await Client(new ScriptedHandler(_ => throw new HttpRequestException("refused"))).EstimateAsync(Lunch, CancellationToken.None);
        var timedOut = await Client(new ScriptedHandler(_ => throw new TaskCanceledException("timeout"))).EstimateAsync(Lunch, CancellationToken.None);

        Assert.Equal(NutritionEstimationFailure.Unavailable, refused.Failure);
        Assert.Equal(NutritionEstimationFailure.Unavailable, timedOut.Failure);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Estimate))).EstimateAsync(Lunch, cancellation.Token));
    }

    [Fact]
    public async Task WithoutAConfiguredService_EveryEstimateIsUnavailable_WithoutNetwork()
    {
        var client = new NutritionEstimationClient(null, null, NullLogger<NutritionEstimationClient>.Instance);

        Assert.Equal(NutritionEstimationFailure.Unavailable, (await client.EstimateAsync(Lunch, CancellationToken.None)).Failure);
    }

    // ---- Service authentication (PROD-AI-001) ----

    [Fact]
    public async Task TheServiceKey_IsSentAsBearerExactlyOnce_OnEveryRequest()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Estimate));
        var client = Client(handler);

        await client.EstimateAsync(Lunch, CancellationToken.None);
        await client.EstimateAsync(Lunch, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal([$"Bearer {ServiceKey}"], request.Authorization));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AConfiguredServiceWithoutAKey_IsRejected(string? key)
    {
        using var httpClient = new HttpClient();

        Assert.Throws<ArgumentException>(() => new NutritionEstimationClient(httpClient, key, NullLogger<NutritionEstimationClient>.Instance));
    }

    [Fact]
    public async Task ARejectedKey_IsUnavailable_AndNeverLogged()
    {
        var logger = new RecordingLogger();
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error": {"code": "unauthorized"}}"""));
        var client = new NutritionEstimationClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") }, ServiceKey, logger);

        var result = await client.EstimateAsync(Lunch, CancellationToken.None);

        Assert.Equal(NutritionEstimationFailure.Unavailable, result.Failure);
        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(ServiceKey, StringComparison.Ordinal));
    }

    // ---- Configuration ----

    [Theory]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000/")]
    [InlineData("https://ai.example.test/lifeos", "https://ai.example.test/lifeos/")]
    [InlineData(" http://localhost:8000/ ", "http://localhost:8000/")]
    public void Configuration_ReadsTheBaseUrl_WithATrailingSlash(string value, string expected)
    {
        var options = NutritionAiConfiguration.Read(Configuration(("NutritionAi:BaseUrl", value), ("NutritionAi:ServiceKey", ServiceKey)));

        Assert.Equal(expected, options.BaseUrl!.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Timeout);
        Assert.Equal(ServiceKey, options.ServiceKey);
    }

    [Fact]
    public void Configuration_ReadsTheServiceKeyTrimmed_AndAProductionTimeout()
    {
        var options = NutritionAiConfiguration.Read(Configuration(("NutritionAi:BaseUrl", "https://lifeos-ai.example.test/"),
            ("NutritionAi:ServiceKey", $" {ServiceKey} "), ("NutritionAi:TimeoutSeconds", "120")));

        Assert.Equal(ServiceKey, options.ServiceKey);
        Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Configuration_ABaseUrlWithoutAServiceKey_FailsAtStartup(string? key)
    {
        var settings = key is null
            ? Configuration(("NutritionAi:BaseUrl", "https://lifeos-ai.example.test/"))
            : Configuration(("NutritionAi:BaseUrl", "https://lifeos-ai.example.test/"), ("NutritionAi:ServiceKey", key));

        var failure = Assert.Throws<InvalidOperationException>(() => NutritionAiConfiguration.Read(settings));

        Assert.Contains("NutritionAi:ServiceKey", failure.Message);
    }

    [Theory]
    [InlineData("short-key")]
    [InlineData("0123456789abcdefghijklmnopqrstu")] // 31 characters
    [InlineData("0123456789abcdefghij klmnopqrstuvwxyz")]
    [InlineData("0123456789abcdefghijklmnopqrstuvwxyz\u00e0")]
    public void Configuration_AWeakServiceKey_FailsAtStartup_WithoutRevealingIt(string key)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => NutritionAiConfiguration.Read(
            Configuration(("NutritionAi:BaseUrl", "https://lifeos-ai.example.test/"), ("NutritionAi:ServiceKey", key))));

        Assert.Contains("NutritionAi:ServiceKey", failure.Message);
        Assert.DoesNotContain(key, failure.Message);
    }

    [Fact]
    public void Configuration_PlainHttpBeyondLoopback_FailsAtStartup()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => NutritionAiConfiguration.Read(
            Configuration(("NutritionAi:BaseUrl", "http://lifeos-ai.example.test/"), ("NutritionAi:ServiceKey", ServiceKey))));

        Assert.Contains("https", failure.Message);
        Assert.DoesNotContain(ServiceKey, failure.Message);
    }

    [Fact]
    public void Configuration_AServiceKeyWithoutABaseUrl_KeepsEstimationDisabled()
    {
        var options = NutritionAiConfiguration.Read(Configuration(("NutritionAi:ServiceKey", ServiceKey)));

        Assert.Null(options.BaseUrl);
        Assert.Null(options.ServiceKey);
    }

    [Fact]
    public void Options_NeverPrintTheServiceKey()
    {
        var options = NutritionAiConfiguration.Read(Configuration(("NutritionAi:BaseUrl", "https://lifeos-ai.example.test/"),
            ("NutritionAi:ServiceKey", ServiceKey)));

        Assert.DoesNotContain(ServiceKey, options.ToString());
        Assert.Contains("lifeos-ai.example.test", options.ToString());
    }

    [Fact]
    public void Configuration_WithoutABaseUrl_DisablesEstimation()
    {
        var options = NutritionAiConfiguration.Read(Configuration(("NutritionAi:TimeoutSeconds", "30")));

        Assert.Null(options.BaseUrl);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Theory]
    [InlineData("NutritionAi:BaseUrl", "127.0.0.1:8000")]
    [InlineData("NutritionAi:BaseUrl", "ftp://host/")]
    [InlineData("NutritionAi:TimeoutSeconds", "0")]
    [InlineData("NutritionAi:TimeoutSeconds", "forever")]
    public void Configuration_InvalidValues_FailAtStartup(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => NutritionAiConfiguration.Read(Configuration((key, value))));
    }

    private static NutritionEstimationClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") }, ServiceKey, NullLogger<NutritionEstimationClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(value => KeyValuePair.Create(value.Key, value.Value))).Build();

    // Authorization: every value of the header as sent (so a duplicate header would show up).
    private sealed record RecordedRequest(HttpMethod Method, string Uri, string[] Authorization, string Body);

    private sealed class RecordingLogger : ILogger<NutritionEstimationClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

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
}
