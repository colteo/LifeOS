using System.Reflection;
using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Persistence;
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

    // AI-007 F3: the interpreter port is implemented only in Infrastructure, outside LifeOS.Infrastructure.Assistant
    // (which stays free of HTTP): the HTTP client of the Python service and the disabled default.
    [Fact]
    public void The_Interpreter_Port_Is_Implemented_Only_In_Infrastructure()
    {
        var implementations = new[] { "LifeOS.Domain", "LifeOS.Application", "LifeOS.Infrastructure", "LifeOS.Contracts", "LifeOS.Api" }
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(IAssistantInterpreter)).GetTypes())
            .ToList();

        Assert.Equal(["AssistantInterpreterClient", "DisabledAssistantInterpreter"], implementations.Select(type => type.Name).Order());
        Assert.All(implementations, type => Assert.Equal("LifeOS.Infrastructure.Interpretation", type.Namespace));
    }

    // AI-007 F3: the turn's Finance capability prepares drafts only through the F2 use cases. It cannot confirm,
    // cancel, open a unit of work, write operations or touch a Finance write port: confirmations and
    // cancellations come only from their explicit endpoints.
    [Fact]
    public void The_Finance_Capability_Cannot_Confirm_Cancel_Or_Write_Directly()
    {
        var result = Types.InAssembly(Assembly.Load("LifeOS.Application"))
            .That().HaveName(nameof(FinanceAssistantModule))
            .ShouldNot()
            .HaveDependencyOnAny(
                typeof(ConfirmFinanceOperationHandler).FullName!,
                typeof(CancelFinanceOperationHandler).FullName!,
                typeof(CreateTransactionHandler).FullName!,
                typeof(IFinanceOperationRepository).FullName!,
                typeof(ITransactionRepository).FullName!,
                typeof(IUnitOfWork).FullName!)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
        Assert.Contains(typeof(UpdateFinanceDraftHandler), typeof(FinanceAssistantModule).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType));
    }

    // AI-007 §16 / ADR-014 §4: nothing the interpreter receives can carry a LifeOS identifier or an instant, and
    // nothing it answers can either (the model can only mention, never designate, an account or a category):
    // the request and the interpretation types (recursively) have no Guid, DateTimeOffset or DateTime member.
    [Fact]
    public void The_Interpreter_Request_And_Answer_Cannot_Carry_Identifiers()
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>([typeof(AssistantInterpretationRequest), typeof(AssistantInterpretation)]);

        while (pending.TryPop(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                var element = propertyType.IsGenericType ? propertyType.GetGenericArguments()[0] : propertyType;

                Assert.False(element == typeof(Guid) || element == typeof(DateTimeOffset) || element == typeof(DateTime),
                    $"{type.Name}.{property.Name} could carry an identifier or an instant.");

                if (element.Namespace == typeof(AssistantInterpretationRequest).Namespace)
                {
                    pending.Push(element);
                }
            }
        }

        Assert.Contains(typeof(AssistantOpenDraftContext), seen);
        Assert.Contains(typeof(AssistantDateField), seen);
    }
}
