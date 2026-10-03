using System.Reflection;
using LifeOS.Application.Nutrition;
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
