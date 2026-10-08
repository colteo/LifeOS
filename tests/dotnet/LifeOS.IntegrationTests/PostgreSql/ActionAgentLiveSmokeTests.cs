using System.Text.Json;
using LifeOS.Application.ActionAgent;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.ActionAgent;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Users;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure;
using LifeOS.Infrastructure.Nutrition;
using LifeOS.UnitTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-002 live provider smoke: SKIPPED unless explicitly configured, never part of a normal run. The real
// .NET agent loop, read tools and repositories (the disposable Testcontainers database) drive the real
// model client against a LOCAL lifeos-ai service, which calls Groq. Synthetic data only; it stops at a
// Pending proposal and never approves, so no budget is written by the agent.
//
// PowerShell, terminal 1 (src/python/lifeos-ai):
//   $env:GROQ_API_KEY = "<key>"; $env:LIFEOS_AI_SERVICE_KEY = "<disposable local key, 32+ chars>"
//   uv run python -m uvicorn lifeos_ai.app:create_app --factory --host 127.0.0.1 --port 8000
// terminal 2 (repository root):
//   $env:LIFEOS_AI_SMOKE_BASE_URL = "http://127.0.0.1:8000/"; $env:LIFEOS_AI_SMOKE_SERVICE_KEY = "<same key>"
//   dotnet test tests/dotnet/LifeOS.IntegrationTests --filter "FullyQualifiedName~ActionAgentLiveSmokeTests" --logger "console;verbosity=detailed"
[Collection(PostgreSqlCollection.Name)]
public class ActionAgentLiveSmokeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    public const string BaseUrlVariable = "LIFEOS_AI_SMOKE_BASE_URL";
    public const string ServiceKeyVariable = "LIFEOS_AI_SMOKE_SERVICE_KEY";

    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    [LiveActionAgentFact]
    public async Task TheRealModel_ReadsThroughDotNetTools_AndEndsWithAValidTerminalDecision()
    {
        var model = new RecordingModel(LiveModel());
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-60));
        await PostgresAssert.InsertAsync(fixture, user);
        var review = WeeklyReview.Create(user.Id, new DateOnly(2026, 10, 4), "Europe/Rome", Now.AddDays(-3), new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 380m, 0m, -380m,
                [new WeeklyExpenseCategory("Groceries", 240m), new WeeklyExpenseCategory("Transport", 140m)])]),
            new WeeklyGymSummary(0, 0, 0, 0, []),
            new WeeklyNutritionSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, [])));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IWeeklyReviewRepository>().TryAddAsync(review, default));
            await scope.ServiceProvider.GetRequiredService<IMonthlyBudgetRepository>().SetAsync(MonthlyBudget.Create(user.Id, 2026, 10, "EUR", 400m), default);
        }

        ActionAgentRunResult run;
        await using (var scope = fixture.CreateScope())
        {
            var services = scope.ServiceProvider;
            var clock = new FixedTimeProvider(Now);
            var budgetStatus = new GetMonthlyBudgetHandler(services.GetRequiredService<IMonthlyBudgetRepository>(),
                services.GetRequiredService<ITransactionRepository>(), clock, null, services.GetRequiredService<IFinancePlanningSnapshotRepository>());
            run = await new RunActionAgentHandler(services.GetRequiredService<IWeeklyReviewRepository>(),
                services.GetRequiredService<IProposedActionRepository>(), new ActionAgentTools(budgetStatus), model, clock)
                .HandleAsync(user.Id, review.Id, default);
        }

        foreach (var (request, result) in model.Steps)
        {
            output.WriteLine($"step {request.Steps.Count + 1}/{request.MaxSteps}: offered {request.Tools.Count} tools, {request.Steps.Count} results in; " +
                $"answer {result.Failure?.ToString() ?? result.Decision!.Kind.ToString()} {result.Decision?.Tool}");
        }

        output.WriteLine($"run: {run.Status} {run.Reason} identity={run.Trace?.Identity} tools=[{string.Join(", ", run.Trace?.ToolCalls ?? [])}] steps={run.Trace?.StepCount}");

        // 5. every real answer passed the service's strict schemas and the .NET client's parsing.
        Assert.All(model.Steps, step => Assert.Null(step.Result.Failure));
        // 1. the first answer is a read-tool call.
        Assert.Equal(AgentDecisionKind.CallTool, model.Steps[0].Result.Decision!.Kind);
        // 2-3. .NET executed it and the next step carried its result to the model.
        Assert.True(model.Steps.Count >= 2);
        Assert.Equal(model.Steps[0].Result.Decision!.Tool, model.Steps[1].Request.Steps[0].Tool);
        Assert.NotEqual(JsonValueKind.Undefined, model.Steps[1].Request.Steps[0].Result.ValueKind);
        // 4. it ended with a terminal decision the loop accepted, within the step limit.
        Assert.Contains(model.Steps[^1].Result.Decision!.Kind, new[] { AgentDecisionKind.ProposeBudgetAdjustment, AgentDecisionKind.NoAction });
        Assert.Contains(run.Status, new[] { ActionAgentRunStatus.Proposed, ActionAgentRunStatus.NoAction });
        Assert.True(model.Steps.Count <= RunActionAgentHandler.MaxSteps);

        if (run.Proposal is { } proposal)
        {
            Assert.Equal(ProposedActionStatus.Pending, proposal.Status);
        }

        // No approval: the budget is exactly what the test set.
        await using (var scope = fixture.CreateScope())
        {
            Assert.Equal(400m, (await scope.ServiceProvider.GetRequiredService<IMonthlyBudgetRepository>().GetAsync(user.Id, 2026, 10, "EUR", default))!.Amount);
        }

        // 6. the final-step rule with the real model: the second step's request (one tool result in), sent as the LAST step
        // (only the terminal tools offered) must still yield a terminal decision.
        var last = model.Steps[1].Request with { MaxSteps = model.Steps[1].Request.Steps.Count + 1 };
        var final = await LiveModel().NextStepAsync(last, default);
        output.WriteLine($"forced last step: {final.Failure?.ToString() ?? final.Decision!.Kind.ToString()}");
        Assert.Null(final.Failure);
        Assert.Contains(final.Decision!.Kind, new[] { AgentDecisionKind.ProposeBudgetAdjustment, AgentDecisionKind.NoAction });
    }

    private static IActionAgentModel LiveModel()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure("Host=unused.invalid;Database=unused", new NutritionAiOptions(
            new Uri(Environment.GetEnvironmentVariable(BaseUrlVariable)!), TimeSpan.FromSeconds(90),
            Environment.GetEnvironmentVariable(ServiceKeyVariable)));

        return services.BuildServiceProvider().GetRequiredService<IActionAgentModel>();
    }

    private sealed class RecordingModel(IActionAgentModel inner) : IActionAgentModel
    {
        public List<(AgentStepRequest Request, AgentStepResult Result)> Steps { get; } = [];

        public async Task<AgentStepResult> NextStepAsync(AgentStepRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.NextStepAsync(request, cancellationToken);
            Steps.Add((request, result));
            return result;
        }
    }
}

// Skips unless both smoke variables are set, so normal and CI runs never contact a provider.
public sealed class LiveActionAgentFactAttribute : FactAttribute
{
    public LiveActionAgentFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ActionAgentLiveSmokeTests.BaseUrlVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ActionAgentLiveSmokeTests.ServiceKeyVariable)))
        {
            Skip = $"Live provider smoke: set {ActionAgentLiveSmokeTests.BaseUrlVariable} and {ActionAgentLiveSmokeTests.ServiceKeyVariable}.";
        }
    }
}
