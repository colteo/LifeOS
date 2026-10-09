using System.Reflection;
using LifeOS.Application.Nutrition;
using LifeOS.Application.WeeklyReviews;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// NUT-002 / ADR-011: AI is an Application port implemented in Infrastructure by an HTTP client of the
// Python service. Domain and Application know no provider, framework or transport. The Python side of
// the boundary (no database, no evaluation-lab imports) is enforced by its own pytest suite
// (src/python/lifeos-ai/tests/test_architecture.py).
public class AiBoundaryArchitectureTests
{
    // Provider SDKs, AI frameworks and the HTTP transport to the Python service.
    private static readonly string[] AiAndTransportNamespaces =
    [
        "Groq",
        "LangChain",
        "LangGraph",
        "OpenAI",
        "Google",
        "Gemini",
        "Anthropic",
        "Microsoft.SemanticKernel",
        "Microsoft.Extensions.AI",
        "Python",
        "System.Net.Http"
    ];

    public static TheoryData<string> CoreAssemblies => new() { "LifeOS.Domain", "LifeOS.Application" };

    [Theory]
    [MemberData(nameof(CoreAssemblies))]
    public void Core_Should_Not_Depend_On_AiProviders_Frameworks_Or_Http(string assemblyName)
    {
        var result = Types.InAssembly(Assembly.Load(assemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(AiAndTransportNamespaces)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_Estimation_Port_Is_Implemented_Only_In_Infrastructure()
    {
        Assert.True(typeof(INutritionEstimationService).IsInterface);

        var implementations = new[] { "LifeOS.Domain", "LifeOS.Application", "LifeOS.Infrastructure", "LifeOS.Contracts" }
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(INutritionEstimationService)).GetTypes())
            .ToList();

        var implementation = Assert.Single(implementations);
        Assert.Equal("LifeOS.Infrastructure", implementation.Assembly.GetName().Name);
        Assert.Equal("LifeOS.Infrastructure.Nutrition", implementation.Namespace);
    }

    // AI-001: the weekly review interpreter follows the same rule.
    [Fact]
    public void The_Weekly_Review_Interpreter_Is_Implemented_Only_In_Infrastructure()
    {
        Assert.True(typeof(IWeeklyReviewInterpreter).IsInterface);

        var implementations = new[] { "LifeOS.Domain", "LifeOS.Application", "LifeOS.Infrastructure", "LifeOS.Contracts" }
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(IWeeklyReviewInterpreter)).GetTypes())
            .ToList();

        var implementation = Assert.Single(implementations);
        Assert.Equal("LifeOS.Infrastructure", implementation.Assembly.GetName().Name);
        Assert.Equal("LifeOS.Infrastructure.WeeklyReviews", implementation.Namespace);
    }

    // AI-001: insights consume only the saved snapshot. Generation can reach nothing but the review, the
    // insights store, the interpreter and the clock: never Finance, Gym or Nutrition.
    [Fact]
    public void Insights_Generation_Depends_Only_On_The_Saved_Review_The_Insights_Store_And_The_Interpreter()
    {
        var parameters = typeof(GenerateWeeklyReviewInsightsHandler).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType);

        Assert.Equal(
            [typeof(IWeeklyReviewRepository), typeof(IWeeklyReviewInsightsRepository), typeof(IWeeklyReviewInterpreter), typeof(TimeProvider)],
            parameters);

        var result = Types.InAssembly(typeof(GenerateWeeklyReviewInsightsHandler).Assembly)
            .That().HaveNameStartingWith("GenerateWeeklyReviewInsights").Or().HaveNameStartingWith("GetWeeklyReviewInsights")
            .ShouldNot().HaveDependencyOnAny("LifeOS.Application.Finance", "LifeOS.Application.Gym", "LifeOS.Application.Nutrition")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // AI-001: insights are added, never updated or deleted, and the review store has no write that
    // could change a saved review.
    [Fact]
    public void Insights_Are_Insert_Only_And_Reviews_Have_No_Update()
    {
        Assert.Equal(["GetAsync", "TryAddAsync"], typeof(IWeeklyReviewInsightsRepository).GetMethods().Select(method => method.Name).Order());
        Assert.DoesNotContain(typeof(IWeeklyReviewRepository).GetMethods(), method =>
            method.Name.Contains("Update") || method.Name.Contains("Replace") || method.Name.StartsWith("Delete", StringComparison.Ordinal));
    }

    [Fact]
    public void Contracts_Do_Not_Expose_Provider_Or_Model_Identity()
    {
        var properties = Types.InAssembly(Assembly.Load("LifeOS.Contracts")).That().ResideInNamespace("LifeOS.Contracts.Nutrition").GetTypes()
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToList();

        Assert.NotEmpty(properties);
        Assert.DoesNotContain(properties, name => name.Contains("Provider") || name.Contains("Model") || name.Contains("Prompt")
            || name.Contains("Confidence"));
    }
}
