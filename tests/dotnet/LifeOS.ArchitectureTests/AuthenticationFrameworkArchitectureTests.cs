using System.Reflection;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// ADR-006: identity is provider-neutral in Domain and Application. Authentication frameworks,
// claims, token libraries and provider SDKs belong to the outer boundary.
public class AuthenticationFrameworkArchitectureTests
{
    private static readonly string[] AuthenticationDependencies =
    [
        "Microsoft.AspNetCore",
        "System.Security.Claims",
        "System.IdentityModel.Tokens.Jwt",
        "Microsoft.IdentityModel",
        "Google"
    ];

    [Fact]
    public void Domain_Should_Not_Depend_On_Authentication_Frameworks()
    {
        AssertNoAuthenticationDependencies(typeof(LifeOS.Domain.AssemblyReference).Assembly);
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Authentication_Frameworks()
    {
        AssertNoAuthenticationDependencies(typeof(LifeOS.Application.AssemblyReference).Assembly);
    }

    private static void AssertNoAuthenticationDependencies(Assembly assembly)
    {
        var result = Types
            .InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(AuthenticationDependencies)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
