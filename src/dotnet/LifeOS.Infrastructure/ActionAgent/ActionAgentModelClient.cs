using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.ActionAgent;
using Microsoft.Extensions.Logging;

namespace LifeOS.Infrastructure.ActionAgent;

// AI-002: client of POST /v1/action-agent/step on the Python AI service (same service, base URL and
// service key as the other AI capabilities: NutritionAiOptions). One call = one model step. Sends the
// tool definitions, the results of the tools LifeOS already executed and the task framing; never user
// ids, review ids or tokens. Every failure is a result, never an exception: the service's status codes
// and messages do not leave this class, and neither tool results, decisions nor the key are ever logged
// (only the HTTP status, latency and identity).
internal sealed class ActionAgentModelClient : IActionAgentModel, IDisposable
{
    private const string StepPath = "v1/action-agent/step";

    // The decision shape this client reads.
    internal const int OutputVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient? _httpClient;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly ILogger<ActionAgentModelClient> _logger;

    public ActionAgentModelClient(HttpClient? httpClient, string? serviceKey, ILogger<ActionAgentModelClient> logger)
    {
        if (httpClient is not null && string.IsNullOrWhiteSpace(serviceKey))
        {
            throw new ArgumentException("A configured AI service requires a service key.", nameof(serviceKey));
        }

        _httpClient = httpClient;
        _authorization = httpClient is null ? null : new AuthenticationHeaderValue("Bearer", serviceKey!.Trim());
        _logger = logger;
    }

    public async Task<AgentStepResult> NextStepAsync(AgentStepRequest request, CancellationToken cancellationToken)
    {
        if (_httpClient is null)
        {
            return AgentStepResult.Failed(AgentStepFailure.Unavailable);
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, StepPath)
            {
                Content = JsonContent.Create(ToPayload(request), options: JsonOptions)
            };
            message.Headers.Authorization = _authorization;

            using var response = await _httpClient.SendAsync(message, cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning("Action agent AI service returned HTTP {StatusCode} after {ElapsedMs} ms.",
                    (int)response.StatusCode, Elapsed(started));

                // 502: the model answered without a valid decision. 422: the service rejected the request
                // (repeating it cannot help). Anything else means the service cannot answer right now.
                return AgentStepResult.Failed(response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.UnprocessableEntity
                    ? AgentStepFailure.InvalidOutput
                    : AgentStepFailure.Unavailable);
            }

            var body = await response.Content.ReadFromJsonAsync<StepResponse>(JsonOptions, cancellationToken);

            if (body is not { OutputVersion: OutputVersion, Provider: { } provider, Model: { } model, PromptVersion: { } prompt, Decision: { } decision }
                || ToDecision(decision) is not { } parsed)
            {
                _logger.LogWarning("Action agent AI service returned an incomplete decision.");
                return AgentStepResult.Failed(AgentStepFailure.InvalidOutput);
            }

            _logger.LogInformation("Action agent step by {Provider} {Model} with prompt {PromptVersion}: {DecisionType} in {ElapsedMs} ms.",
                provider, model, prompt, decision.Type, Elapsed(started));

            return AgentStepResult.Success(parsed, new AgentModelIdentity(provider, model, prompt));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning("Action agent AI service returned an unreadable decision.");
            return AgentStepResult.Failed(AgentStepFailure.InvalidOutput);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("Action agent AI service is unreachable or timed out ({ExceptionType}) after {ElapsedMs} ms.",
                exception.GetType().Name, Elapsed(started));
            return AgentStepResult.Failed(AgentStepFailure.Unavailable);
        }
    }

    public void Dispose() => _httpClient?.Dispose();

    // The exact external payload (see the service's ActionAgentStepRequest).
    internal static StepRequest ToPayload(AgentStepRequest request) => new(
        request.ToolSchemaVersion,
        new ContextPayload(request.Context.CurrentMonth, [.. request.Context.TargetMonths]),
        request.Tools.Select(tool => new ToolPayload(tool.Name, tool.Description, tool.Parameters)).ToList(),
        request.Steps.Select(step => new StepPayload(step.Tool, step.Arguments, step.Result)).ToList(),
        request.MaxSteps);

    // Only the three documented shapes; anything else is not a decision.
    private static AgentDecision? ToDecision(DecisionPayload decision) => decision switch
    {
        { Type: "call_tool", Tool: { Length: > 0 } tool, Arguments: { ValueKind: JsonValueKind.Object } arguments } =>
            AgentDecision.CallTool(tool, arguments.Clone()),
        { Type: "propose_budget_adjustment", Proposal: { Year: { } year, Month: { } month, Currency: { } currency, ProposedAmount: { } amount, Rationale: { } rationale } } =>
            AgentDecision.Propose(new AgentBudgetProposal(year, month, currency, amount, rationale)),
        { Type: "no_action", Reason: { } reason } => AgentDecision.Finish(reason),
        _ => null
    };

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    internal sealed record StepRequest(string ToolSchemaVersion, ContextPayload Context, List<ToolPayload> Tools, List<StepPayload> Steps, int MaxSteps);

    internal sealed record ContextPayload(string CurrentMonth, List<string> TargetMonths);

    internal sealed record ToolPayload(string Name, string Description, JsonElement Parameters);

    internal sealed record StepPayload(string Tool, JsonElement Arguments, JsonElement Result);

    private sealed record StepResponse(int? OutputVersion, string? Provider, string? Model, string? PromptVersion, DecisionPayload? Decision);

    private sealed record DecisionPayload(string? Type, string? Tool, JsonElement? Arguments, ProposalPayload? Proposal, string? Reason);

    private sealed record ProposalPayload(int? Year, int? Month, string? Currency, decimal? ProposedAmount, string? Rationale);
}
