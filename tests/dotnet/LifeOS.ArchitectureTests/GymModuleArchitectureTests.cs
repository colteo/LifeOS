using System.Reflection;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// GYM-001: the Gym module must not depend on Finance, in any layer.
public class GymModuleArchitectureTests
{
    public static TheoryData<string, string> GymAndFinanceNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Gym" },
        { "LifeOS.Application", "LifeOS.Application.Gym" },
        { "LifeOS.Infrastructure", "LifeOS.Infrastructure.Gym" },
        { "LifeOS.Contracts", "LifeOS.Contracts.Gym" }
    };

    [Theory]
    [MemberData(nameof(GymAndFinanceNamespaces))]
    public void Gym_Should_Not_Depend_On_Finance(string assemblyName, string gymNamespace)
    {
        var financeNamespace = assemblyName + ".Finance";

        var gymTypes = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(gymNamespace);

        // Guard against a vacuous pass: the Gym namespace exists in this assembly.
        Assert.NotEmpty(gymTypes.GetTypes());

        var result = gymTypes
            .ShouldNot()
            .HaveDependencyOn(financeNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
