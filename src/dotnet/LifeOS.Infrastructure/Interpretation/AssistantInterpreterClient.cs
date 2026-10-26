using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LifeOS.Application.Assistant;
using Microsoft.Extensions.Logging;

namespace LifeOS.Infrastructure.Interpretation;

// AI-007 F3: the assistant interpreter's settings. Disabled by default: no message leaves LifeOS unless
// AssistantAi:Enabled is set explicitly. Enabling it only makes LifeOS call the Python service
// (NutritionAi:BaseUrl, ADR-011), which selects the model provider by its own explicit configuration; nothing
// here can reach a model provider directly or hold a provider key.
public sealed record AssistantAiOptions(bool Enabled, TimeSpan Timeout)
{
    // Below the turn lease minus the processor's completion margin (90 s − 15 s), so the service's answer
    // can still be stored by the attempt that asked for it.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    public static readonly TimeSpan MaxTimeout = AssistantTurnPolicy.LeaseDuration - AssistantTurnProcessor.CompletionMargin;

    public static AssistantAiOptions Disabled { get; } = new(false, DefaultTimeout);
}

// The default interpreter: answers Unavailable at once, without any network call. Every turn then fails with
// assistant_unavailable, exactly as in F1/F2.
internal sealed class DisabledAssistantInterpreter : IAssistantInterpreter
{
    public Task<AssistantInterpretationResult> InterpretAsync(AssistantInterpretationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(AssistantInterpretationResult.Failed(AssistantInterpretationFailure.Unavailable));
}

// AI-007 F3: client of POST /v1/assistant/interpret on the Python AI service (same base URL and service key
// as the other AI capabilities). Sends exactly the minimised request built by the Application layer: the
// message, the locale, the open draft's filled/missing field names and the user's category names — no
// identifier, no account, no amount history (the request types contain none). Lives outside
// LifeOS.Infrastructure.Assistant, which stays free of HTTP (AssistantArchitectureTests).
//
// Every failure is a result, never an exception (except the caller's own cancellation): 200 with a readable
// body → the interpretation (validated again by the processor); 502 interpretation_refused → Refused; other
// 502 or 422 → InvalidOutput; anything else (503, 500, 401, …) or a transport error → Unavailable; the
// HttpClient timeout → Timeout. Logs: status, latency, provider/model/prompt identity; never the message,
// the categories, the interpretation, the service's error body or the key.
internal sealed class AssistantInterpreterClient : IAssistantInterpreter, IDisposable
{
    private const string InterpretPath = "v1/assistant/interpret";
    private const string RefusedCode = "interpretation_refused";

    // Strict both ways, as the canonical contract (docs/contracts/assistant-interpretation/v1) and Pydantic:
    // snake_case names and enum values, unknown members refused, every member present (null where allowed,
    // never omitted), nulls only where allowed.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RequireEveryMember } }
    };

    private readonly HttpClient _httpClient;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly ILogger<AssistantInterpreterClient> _logger;

    public AssistantInterpreterClient(HttpClient httpClient, string? serviceKey, ILogger<AssistantInterpreterClient> logger)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
        {
            throw new ArgumentException("A configured AI service requires a service key.", nameof(serviceKey));
        }

        _httpClient = httpClient;
        _authorization = new AuthenticationHeaderValue("Bearer", serviceKey.Trim());
        _logger = logger;
    }

    public async Task<AssistantInterpretationResult> InterpretAsync(AssistantInterpretationRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, InterpretPath)
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            message.Headers.Authorization = _authorization;

            using var response = await _httpClient.SendAsync(message, cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning("Assistant AI service returned HTTP {StatusCode} after {ElapsedMs} ms.", (int)response.StatusCode, Elapsed(started));

                return AssistantInterpretationResult.Failed(response.StatusCode switch
                {
                    HttpStatusCode.BadGateway when await ErrorCodeAsync(response, cancellationToken) == RefusedCode => AssistantInterpretationFailure.Refused,
                    HttpStatusCode.BadGateway or HttpStatusCode.UnprocessableEntity => AssistantInterpretationFailure.InvalidOutput,
                    _ => AssistantInterpretationFailure.Unavailable
                });
            }

            var body = await response.Content.ReadFromJsonAsync<InterpretResponse>(JsonOptions, cancellationToken);

            if (body is not { OutputVersion: AssistantInterpretationProtocol.Version, Provider: { } provider, Model: { } model, PromptVersion: { } prompt,
                    Interpretation: { } interpretation })
            {
                _logger.LogWarning("Assistant AI service returned an incomplete interpretation.");
                return AssistantInterpretationResult.Failed(AssistantInterpretationFailure.InvalidOutput);
            }

            _logger.LogInformation("Assistant message interpreted by {Provider} {Model} with prompt {PromptVersion} in {ElapsedMs} ms.",
                provider, model, prompt, Elapsed(started));

            return AssistantInterpretationResult.Success(interpretation);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning("Assistant AI service returned an unreadable interpretation.");
            return AssistantInterpretationResult.Failed(AssistantInterpretationFailure.InvalidOutput);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Assistant AI service timed out after {ElapsedMs} ms.", Elapsed(started));
            return AssistantInterpretationResult.Failed(AssistantInterpretationFailure.Timeout);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("Assistant AI service is unreachable ({ExceptionType}) after {ElapsedMs} ms.", exception.GetType().Name, Elapsed(started));
            return AssistantInterpretationResult.Failed(AssistantInterpretationFailure.Unavailable);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    // Only the stable code of a failure body (`{"error": {"code": "..."}}`) is read; nothing else of it.
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static void RequireEveryMember(JsonTypeInfo type)
    {
        if (type.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in type.Properties)
        {
            property.IsRequired = true;
        }
    }

    private sealed record InterpretResponse(int? OutputVersion, string? Provider, string? Model, string? PromptVersion,
        AssistantInterpretation? Interpretation);
}
