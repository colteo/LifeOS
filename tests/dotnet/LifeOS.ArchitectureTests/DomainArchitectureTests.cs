using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

public class DomainArchitectureTests
{
    private const string DomainNamespace = "LifeOS.Domain";
    private const string ApplicationNamespace = "LifeOS.Application";
    private const string InfrastructureNamespace = "LifeOS.Infrastructure";

    [Fact]
    public void Domain_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types
            .InAssembly(typeof(LifeOS.Domain.AssemblyReference).Assembly)
            .ShouldNot()
            .HaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Domain_Should_Not_Depend_On_Application()
    {
        var result = Types
            .InAssembly(typeof(LifeOS.Domain.AssemblyReference).Assembly)
            .ShouldNot()
            .HaveDependencyOn(ApplicationNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Domain_Should_Not_Depend_On_EntityFramework()
    {
        var result = Types
            .InAssembly(typeof(LifeOS.Domain.AssemblyReference).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }
}