using System.Reflection;
using LifeOS.Application.Assistant;
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
}
