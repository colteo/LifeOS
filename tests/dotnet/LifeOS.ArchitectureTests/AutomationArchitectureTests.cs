using System.Reflection;
using LifeOS.Application.Automation;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// AUTO-001 §14 and WP2: the automation core knows no business module, PostgreSQL claim logic stays in
// Infrastructure, the API adapter is transport/auth only, no push provider exists yet, and no
// business automation handler ships.
public class AutomationArchitectureTests
{
    private static readonly string[] ProductionAssemblies =
        ["LifeOS.Domain", "LifeOS.Application", "LifeOS.Infrastructure", "LifeOS.Contracts", "LifeOS.Api"];

    public static TheoryData<string, string> CoreNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Automation" },
        { "LifeOS.Application", "LifeOS.Application.Automation" }
    };

    [Theory]
    [MemberData(nameof(CoreNamespaces))]
    public void AutomationCore_Should_Not_Depend_On_BusinessModules(string assemblyName, string automationNamespace)
    {
        var core = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(automationNamespace);

        // Guard against a vacuous pass.
        Assert.NotEmpty(core.GetTypes());

        var result = core
            .ShouldNot()
            .HaveDependencyOnAny(
                "LifeOS.Domain.Finance", "LifeOS.Domain.Gym", "LifeOS.Domain.Nutrition",
                "LifeOS.Application.Finance", "LifeOS.Application.Gym", "LifeOS.Application.Nutrition")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(CoreNamespaces))]
    public void AutomationCore_Should_Not_Depend_On_Persistence_Or_Hosting(string assemblyName, string automationNamespace)
    {
        var result = Types.InAssembly(Assembly.Load(assemblyName))
            .That().ResideInNamespace(automationNamespace)
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "LifeOS.Infrastructure", "LifeOS.Api")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // The store port is implemented only in Infrastructure (where the PostgreSQL claim SQL lives).
    [Fact]
    public void ExecutionStore_Is_Implemented_Only_In_Infrastructure()
    {
        var implementations = ProductionAssemblies
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(IAutomationExecutionStore)).GetTypes())
            .ToList();

        Assert.Equal(["LifeOS.Infrastructure.Automation.AutomationExecutionStore"], implementations.Select(type => type.FullName));
    }

    // The tick endpoint is an adapter: transport and authentication, no persistence.
    [Fact]
    public void ApiAutomation_Should_Not_Depend_On_Persistence()
    {
        var api = Types.InAssembly(Assembly.Load("LifeOS.Api")).That().ResideInNamespace("LifeOS.Api.Automation");

        Assert.NotEmpty(api.GetTypes());

        var result = api
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Microsoft.EntityFrameworkCore", "LifeOS.Infrastructure", "LifeOS.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // AUTO-001 registers zero business handlers: no production type implements IAutomationHandler.
    [Fact]
    public void No_Production_AutomationHandler_Exists()
    {
        var handlers = ProductionAssemblies
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(IAutomationHandler)).GetTypes());

        Assert.Empty(handlers);
    }

    // Push (FCM HTTP v1 + Google.Apis.Auth) arrives with WP3, and then only in Infrastructure.
    [Fact]
    public void No_Push_Provider_Is_Referenced_Yet()
    {
        string[] pushNamespaces = ["Google.Apis", "FirebaseAdmin", "Firebase"];

        foreach (var name in ProductionAssemblies)
        {
            var assembly = Assembly.Load(name);

            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                pushNamespaces.Any(push => reference.Name!.StartsWith(push, StringComparison.Ordinal)));

            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(pushNamespaces).GetResult();
            Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
        }
    }
}
