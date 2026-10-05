using System.Reflection;
using System.Runtime.CompilerServices;
using LifeOS.Application.Automation;
using LifeOS.Application.Notifications;
using LifeOS.Application.Persistence;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// AUTO-001 §14, WP2 and WP3A: the automation and notification core knows no business module,
// PostgreSQL claim logic stays in Infrastructure, the API adapters are transport/auth only, no push
// provider (FCM, Google.Apis.Auth, Firebase) exists yet, and no business automation handler ships.
public class AutomationArchitectureTests
{
    private static readonly string[] ProductionAssemblies =
        ["LifeOS.Domain", "LifeOS.Application", "LifeOS.Infrastructure", "LifeOS.Contracts", "LifeOS.Api"];

    public static TheoryData<string, string> CoreNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Automation" },
        { "LifeOS.Application", "LifeOS.Application.Automation" },
        { "LifeOS.Domain", "LifeOS.Domain.Notifications" },
        { "LifeOS.Application", "LifeOS.Application.Notifications" }
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

    // The persistence ports are implemented only in Infrastructure (PostgreSQL claims, transactions).
    [Theory]
    [InlineData(typeof(INotificationDeliveryStore), "LifeOS.Infrastructure.Notifications.NotificationDeliveryStore")]
    [InlineData(typeof(IDeviceRegistrationRepository), "LifeOS.Infrastructure.Notifications.DeviceRegistrationRepository")]
    [InlineData(typeof(IUnitOfWork), "LifeOS.Infrastructure.Persistence.EfUnitOfWork")]
    public void NotificationPorts_Are_Implemented_Only_In_Infrastructure(Type port, string implementation)
    {
        var implementations = ProductionAssemblies
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(port).GetTypes())
            .Select(type => type.FullName);

        Assert.Equal([implementation], implementations);
    }

    // WP3A ships no push provider: dispatch (tick Phase A) stays disabled until the FCM sender (WP3B).
    [Fact]
    public void No_Production_PushNotificationSender_Exists_Yet()
    {
        var senders = ProductionAssemblies
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(IPushNotificationSender)).GetTypes());

        Assert.Empty(senders);
    }

    // Phase A is part of the automation core and depends only on the notification core.
    [Fact]
    public void Tick_Uses_The_Notification_Core_Only_Through_Application_Types()
    {
        var tick = typeof(RunAutomationTick);

        Assert.Contains(tick.GetConstructors().Single().GetParameters(), parameter => parameter.ParameterType == typeof(NotificationDispatcher));
        Assert.Equal("LifeOS.Application.Notifications", typeof(NotificationDispatcher).Namespace);
    }

    // No Firebase on the Android side yet (WP3B): no package, no google-services.json, no Firebase code.
    [Fact]
    public void App_Has_No_Firebase_Integration_Yet()
    {
        var app = AppDirectory();

        Assert.True(Directory.Exists(app), app);
        Assert.DoesNotContain("Firebase", File.ReadAllText(Path.Combine(app, "LifeOS.App.csproj")), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(app, "google-services.json", SearchOption.AllDirectories));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories).Where(file => !IsBuildOutput(file)),
            file => File.ReadAllText(file).Contains("Firebase", StringComparison.Ordinal));
    }

    private static string AppDirectory([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "dotnet", "LifeOS.App"));

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // The tick and device endpoints are adapters: transport and authentication, no persistence.
    [Theory]
    [InlineData("LifeOS.Api.Automation")]
    [InlineData("LifeOS.Api.Notifications")]
    public void ApiAdapters_Should_Not_Depend_On_Persistence(string apiNamespace)
    {
        var api = Types.InAssembly(Assembly.Load("LifeOS.Api")).That().ResideInNamespace(apiNamespace);

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
