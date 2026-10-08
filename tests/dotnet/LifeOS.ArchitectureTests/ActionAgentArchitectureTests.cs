using System.Reflection;
using LifeOS.Application.ActionAgent;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.ActionAgent;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// AI-002: the Action Agent's safety boundaries, machine-enforced. The model is an Application port
// implemented only in Infrastructure; the read tools and the agent run cannot reach any write; only the
// approval handler reaches the Finance budget command, and it never consults the model; proposals have
// no way to change their payload; the action type is an allowlist of one.
public class ActionAgentArchitectureTests
{
    private const string SetBudget = "LifeOS.Application.Finance.Budgets.SetMonthlyBudgetHandler";
    private const string DeleteBudget = "LifeOS.Application.Finance.Budgets.DeleteMonthlyBudgetHandler";
    private const string BudgetRepository = "LifeOS.Application.Finance.Budgets.IMonthlyBudgetRepository";
    private const string UnitOfWork = "LifeOS.Application.Persistence.IUnitOfWork";

    private static readonly Assembly Application = typeof(RunActionAgentHandler).Assembly;

    [Fact]
    public void The_Model_Port_Is_Implemented_Only_In_Infrastructure()
    {
        Assert.True(typeof(IActionAgentModel).IsInterface);

        var implementations = new[] { "LifeOS.Domain", "LifeOS.Application", "LifeOS.Infrastructure", "LifeOS.Contracts", "LifeOS.Api" }
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(IActionAgentModel)).GetTypes())
            .ToList();

        var implementation = Assert.Single(implementations);
        Assert.Equal("LifeOS.Infrastructure.ActionAgent", implementation.Namespace);
    }

    // Read-only by construction: the tools hold only the budget QUERY handler.
    [Fact]
    public void The_Read_Tools_Depend_Only_On_The_Budget_Query()
    {
        Assert.Equal([typeof(GetMonthlyBudgetHandler)], Constructor<ActionAgentTools>());

        var result = Types.InAssembly(Application).That().HaveName(nameof(ActionAgentTools))
            .ShouldNot().HaveDependencyOnAny(SetBudget, DeleteBudget, BudgetRepository, UnitOfWork, "LifeOS.Application.ActionAgent.IProposedActionRepository")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // The agent run can read the review, store a Pending proposal, call the read tools and the model.
    // It has no path to any Finance write.
    [Fact]
    public void The_Agent_Run_Cannot_Reach_A_Finance_Write()
    {
        Assert.Equal(
            [typeof(IWeeklyReviewRepository), typeof(IProposedActionRepository), typeof(ActionAgentTools), typeof(IActionAgentModel), typeof(TimeProvider)],
            Constructor<RunActionAgentHandler>());

        var result = Types.InAssembly(Application).That().HaveNameStartingWith(nameof(RunActionAgentHandler))
            .ShouldNot().HaveDependencyOnAny(SetBudget, DeleteBudget, BudgetRepository, UnitOfWork)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // Execution happens only on the user's approval, through the existing Finance command, from the
    // stored proposal: the approval path never calls the model.
    [Fact]
    public void Only_The_Approval_Reaches_The_Budget_Command_And_It_Never_Calls_The_Model()
    {
        var writers = Types.InAssembly(Application).That().ResideInNamespace("LifeOS.Application.ActionAgent")
            .And().HaveDependencyOn(SetBudget)
            .GetTypes()
            .Select(type => type.Name)
            .Where(name => !name.StartsWith('<'))
            .ToList();

        Assert.Equal([nameof(ApproveProposedActionHandler)], writers.Distinct());

        var result = Types.InAssembly(Application).That().HaveNameStartingWith(nameof(ApproveProposedActionHandler))
            .Or().HaveNameStartingWith(nameof(RejectProposedActionHandler))
            .ShouldNot().HaveDependencyOnAny("LifeOS.Application.ActionAgent.IActionAgentModel", "LifeOS.Application.ActionAgent.ActionAgentTools")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Proposals_Can_Be_Added_And_Change_Status_But_Never_Change_Their_Payload()
    {
        Assert.Equal(["GetAsync", "GetLatestForReviewAsync", "TryAddAsync", "TryUpdateStatusAsync"],
            typeof(IProposedActionRepository).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));

        Assert.All(typeof(ProposedAction).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void The_Action_Type_Is_An_Allowlist_Of_One()
    {
        Assert.Equal([ProposedActionType.MonthlyBudgetAdjustment], Enum.GetValues<ProposedActionType>());
    }

    private static Type[] Constructor<T>() =>
        typeof(T).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType).ToArray();
}
