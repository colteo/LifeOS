using System.Reflection;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// HYD-001 §10-11: Hydration is the single source of truth for water. It references no Habits or
// Reminders type (they read it through IHydrationDayReader, never the other way round), no other
// business module, no AI and no HTTP client.
public class HydrationModuleArchitectureTests
{
    public static TheoryData<string, string> HydrationNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Nutrition.Hydration" },
        { "LifeOS.Application", "LifeOS.Application.Nutrition.Hydration" },
        { "LifeOS.Infrastructure", "LifeOS.Infrastructure.Nutrition.Hydration" },
        { "LifeOS.Contracts", "LifeOS.Contracts.Nutrition.Hydration" }
    };

    [Theory]
    [MemberData(nameof(HydrationNamespaces))]
    public void Hydration_Should_Not_Depend_On_Habits_Reminders_Other_Modules_Ai_Or_Http(string assemblyName, string hydrationNamespace)
    {
        var hydrationTypes = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(hydrationNamespace);

        // Guard against a vacuous pass: the Hydration namespace exists in this assembly.
        Assert.NotEmpty(hydrationTypes.GetTypes());

        var result = hydrationTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                assemblyName + ".Habits",
                assemblyName + ".Reminders",
                assemblyName + ".Finance",
                assemblyName + ".Gym",
                assemblyName + ".Journal",
                assemblyName + ".Memory",
                assemblyName + ".WeeklyReviews",
                assemblyName + ".ActionAgent",
                assemblyName + ".Automation",
                assemblyName + ".Notifications",
                "LifeOS.Application.Nutrition.INutritionEstimationService",
                "LifeOS.Infrastructure.Nutrition.NutritionEstimationClient",
                "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void HydrationDomain_IsFrameworkIndependent()
    {
        var result = Types.InAssembly(Assembly.Load("LifeOS.Domain")).That().ResideInNamespace("LifeOS.Domain.Nutrition.Hydration")
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "LifeOS.Application", "LifeOS.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // The read port other modules may use exposes no write: only GetDaysAsync.
    [Fact]
    public void TheDayReader_IsReadOnly()
    {
        var reader = Assembly.Load("LifeOS.Application").GetType("LifeOS.Application.Nutrition.Hydration.IHydrationDayReader", throwOnError: true)!;

        Assert.Equal(["GetDaysAsync"], reader.GetMethods().Select(method => method.Name));
    }
}
