using System.Reflection;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// NUT-001: the Nutrition module depends on neither Finance nor Gym, in any layer.
public class NutritionModuleArchitectureTests
{
    public static TheoryData<string, string> NutritionNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Nutrition" },
        { "LifeOS.Application", "LifeOS.Application.Nutrition" },
        { "LifeOS.Infrastructure", "LifeOS.Infrastructure.Nutrition" },
        { "LifeOS.Contracts", "LifeOS.Contracts.Nutrition" }
    };

    [Theory]
    [MemberData(nameof(NutritionNamespaces))]
    public void Nutrition_Should_Not_Depend_On_Finance_Or_Gym(string assemblyName, string nutritionNamespace)
    {
        var nutritionTypes = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(nutritionNamespace);

        // Guard against a vacuous pass: the Nutrition namespace exists in this assembly.
        Assert.NotEmpty(nutritionTypes.GetTypes());

        var result = nutritionTypes
            .ShouldNot()
            .HaveDependencyOnAny(assemblyName + ".Finance", assemblyName + ".Gym")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // NUT-003: daily targets are manual. Their domain, use cases and persistence never reach the AI
    // estimation port, its client or its inputs.
    [Fact]
    public void Targets_Should_Not_Depend_On_Ai_Estimation()
    {
        var targetTypes = Types.InAssemblies([Assembly.Load("LifeOS.Domain"), Assembly.Load("LifeOS.Application"), Assembly.Load("LifeOS.Infrastructure")])
            .That()
            .HaveNameMatching("NutritionTarget");

        Assert.Contains(targetTypes.GetTypes(), type => type.Name == "NutritionTarget");
        Assert.Contains(targetTypes.GetTypes(), type => type.Name == "SetNutritionTargetHandler");
        Assert.Contains(targetTypes.GetTypes(), type => type.Name == "NutritionTargetRepository");

        var result = targetTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                "LifeOS.Application.Nutrition.INutritionEstimationService",
                "LifeOS.Application.Nutrition.MealNutritionEstimation",
                "LifeOS.Application.Nutrition.MealEstimationInput",
                "LifeOS.Infrastructure.Nutrition.NutritionEstimationClient",
                "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
