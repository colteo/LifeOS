using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

public class ApplicationArchitectureTests
{
    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types
            .InAssembly(typeof(LifeOS.Application.AssemblyReference).Assembly)
            .ShouldNot()
            .HaveDependencyOn("LifeOS.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Application_Should_Not_Depend_On_EntityFramework()
    {
        var result = Types
            .InAssembly(typeof(LifeOS.Application.AssemblyReference).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Application_Should_Not_Depend_On_AspNetCore()
    {
        var result = Types
            .InAssembly(typeof(LifeOS.Application.AssemblyReference).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }
}