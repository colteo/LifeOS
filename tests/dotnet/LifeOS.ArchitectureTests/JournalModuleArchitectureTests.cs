using System.Reflection;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// JRN-001: the Journal module depends on no other business module, in any layer, and its core has no
// HTTP/AI client dependency (AI-004 Journal RAG is deferred and must arrive through explicit ports).
public class JournalModuleArchitectureTests
{
    public static TheoryData<string, string> JournalNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Journal" },
        { "LifeOS.Application", "LifeOS.Application.Journal" },
        { "LifeOS.Infrastructure", "LifeOS.Infrastructure.Journal" },
        { "LifeOS.Contracts", "LifeOS.Contracts.Journal" }
    };

    [Theory]
    [MemberData(nameof(JournalNamespaces))]
    public void Journal_Should_Not_Depend_On_Other_Modules_Or_Http(string assemblyName, string journalNamespace)
    {
        var journalTypes = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(journalNamespace);

        // Guard against a vacuous pass: the Journal namespace exists in this assembly.
        Assert.NotEmpty(journalTypes.GetTypes());

        var result = journalTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                assemblyName + ".Finance",
                assemblyName + ".Gym",
                assemblyName + ".Nutrition",
                assemblyName + ".WeeklyReviews",
                "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
