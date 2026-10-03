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
}
