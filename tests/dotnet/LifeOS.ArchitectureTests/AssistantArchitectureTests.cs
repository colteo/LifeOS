using System.Reflection;
using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// AI-007 F1: the Assistant core knows no business module (Finance arrives in F2 through its existing
// use cases), calls no AI and has no HTTP client; and the processor-only turn operations, which close a
// turn for a given attempt, are never reachable from the HTTP adapter.
public class AssistantArchitectureTests
{
    public static TheoryData<string, string> AssistantNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Assistant" },
        { "LifeOS.Application", "LifeOS.Application.Assistant" },
        { "LifeOS.Infrastructure", "LifeOS.Infrastructure.Assistant" }
    };

    [Theory]
    [MemberData(nameof(AssistantNamespaces))]
    public void Assistant_Should_Not_Depend_On_Business_Modules_Ai_Or_Http(string assemblyName, string assistantNamespace)
    {
        var assistantTypes = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(assistantNamespace);

        // Guard against a vacuous pass.
        Assert.NotEmpty(assistantTypes.GetTypes());

        var result = assistantTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                assemblyName + ".Finance",
                assemblyName + ".Nutrition",
                assemblyName + ".Habits",
                assemblyName + ".Gym",
                assemblyName + ".Journal",
                assemblyName + ".Memory",
                assemblyName + ".WeeklyReviews",
                assemblyName + ".ActionAgent",
                assemblyName + ".Reminders",
                "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_Api_Cannot_Complete_Or_Fail_A_Turn()
    {
        var result = Types.InAssembly(Assembly.Load("LifeOS.Api"))
            .ShouldNot()
            .HaveDependencyOnAny(
                typeof(CompleteAssistantTurnHandler).FullName!,
                typeof(CompleteAssistantTurnCommand).FullName!,
                typeof(FailAssistantTurnHandler).FullName!,
                typeof(FailAssistantTurnCommand).FullName!)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // F1: the HTTP adapter reaches turns only through AssistantTurnProcessor, which closes every attempt it
    // starts; accepting a turn directly would leave it Processing with nothing to answer it.
    [Fact]
    public void The_Api_Accepts_Turns_Only_Through_The_Processor()
    {
        var api = Types.InAssembly(Assembly.Load("LifeOS.Api"));

        var result = api
            .ShouldNot()
            .HaveDependencyOnAny(typeof(SendAssistantMessageHandler).FullName!, typeof(RetryAssistantTurnHandler).FullName!)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
        // Guard against a vacuous pass: the endpoints do use the processor.
        Assert.NotEmpty(api.That().HaveDependencyOn(typeof(AssistantTurnProcessor).FullName!).GetTypes());
    }

    // AI-007 F2: the assistant's HTTP adapter reaches Finance only through the operation use cases. It cannot
    // create a transaction, open a unit of work or write operations directly: a transaction is recorded only by
    // the confirmation use case.
    [Fact]
    public void The_Assistant_Endpoints_Cannot_Write_Finance_Or_Operations_Directly()
    {
        var endpoints = Types.InAssembly(Assembly.Load("LifeOS.Api")).That().ResideInNamespace("LifeOS.Api.Assistant");

        var result = endpoints
            .ShouldNot()
            .HaveDependencyOnAny(
                "LifeOS.Application.Finance.Transactions",
                "LifeOS.Application.Persistence",
                typeof(IFinanceOperationRepository).FullName!)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
        Assert.NotEmpty(endpoints.And().HaveDependencyOn(typeof(ConfirmFinanceOperationHandler).FullName!).GetTypes());
    }

    // The Finance capability writes Finance through CreateTransactionHandler only, and only from the
    // confirmation; no other part of it touches a Finance write port.
    [Fact]
    public void Only_The_Confirmation_Creates_Transactions_And_Only_Through_CreateTransactionHandler()
    {
        var capability = Types.InAssembly(Assembly.Load("LifeOS.Application")).That().ResideInNamespace("LifeOS.Application.Finance.Assistant");

        // Compiler-generated state machines count as their declaring type.
        var users = capability.And().HaveDependencyOn(typeof(CreateTransactionHandler).FullName!).GetTypes()
            .Select(type => type.FullName!.Split('+')[0]).Distinct().ToList();
        Assert.Equal([typeof(ConfirmFinanceOperationHandler).FullName], users);

        var result = capability
            .ShouldNot()
            .HaveDependencyOnAny(typeof(ITransactionRepository).FullName!, "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
