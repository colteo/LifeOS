using System.Collections.Concurrent;
using System.Text.Json;
using LifeOS.Application.ActionAgent;

namespace LifeOS.UnitTests.Fakes;

// The AI-002 model port without any AI: answers each step from a script (one answer per call, the last
// one repeating) and records every request, so tests can assert exactly what the model would have seen
// and how many steps were taken.
internal sealed class FakeActionAgentModel : IActionAgentModel
{
    private readonly ConcurrentQueue<AgentStepRequest> _requests = new();
    private readonly Lock _lock = new();
    private Queue<AgentStepResult> _script = new();
    private AgentStepResult? _last;

    public static readonly AgentModelIdentity Identity = new("fake", "fake-model", "action-agent-v1");

    public FakeActionAgentModel()
    {
        Script(Finish("Nothing to change."));
    }

    public IReadOnlyList<AgentStepRequest> Requests => _requests.ToList();

    // Called before each answer; lets a test interleave other work with an in-flight step.
    public Func<AgentStepRequest, Task>? BeforeAnswer { get; set; }

    public void Script(params AgentStepResult[] answers)
    {
        lock (_lock)
        {
            _script = new Queue<AgentStepResult>(answers);
            _last = answers[^1];
        }
    }

    public static AgentStepResult Call(string tool, string arguments = "{}") =>
        AgentStepResult.Success(AgentDecision.CallTool(tool, Json(arguments)), Identity);

    public static AgentStepResult ReadReview() => Call(ActionAgentTools.GetWeeklyReview);

    public static AgentStepResult ReadBudget(int year = 2026, int month = 10, string currency = "EUR") =>
        Call(ActionAgentTools.GetBudgetStatus, $$"""{"year":{{year}},"month":{{month}},"currency":"{{currency}}"}""");

    public static AgentStepResult Propose(decimal amount, int year = 2026, int month = 10, string currency = "EUR",
        string rationale = "You spent 380 of 400 EUR with 90 EUR still expected.") =>
        AgentStepResult.Success(AgentDecision.Propose(new AgentBudgetProposal(year, month, currency, amount, rationale)), Identity);

    public static AgentStepResult Finish(string reason) => AgentStepResult.Success(AgentDecision.Finish(reason), Identity);

    public static AgentStepResult Unavailable => AgentStepResult.Failed(AgentStepFailure.Unavailable);

    public static AgentStepResult InvalidOutput => AgentStepResult.Failed(AgentStepFailure.InvalidOutput);

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    public async Task<AgentStepResult> NextStepAsync(AgentStepRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);

        if (BeforeAnswer is { } before)
        {
            await before(request);
        }

        lock (_lock)
        {
            return _script.Count > 0 ? _script.Dequeue() : _last!;
        }
    }
}
