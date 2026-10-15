using System.Reflection;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// REM-001 §13: the Reminders module depends on no other business module (R5 will read Habits and
// Hydration only through their reader ports), has no HTTP or AI client, and its Domain is framework
// independent. It plugs into the automation and notification core, never the other way round
// (AutomationArchitectureTests).
public class RemindersModuleArchitectureTests
{
    public static TheoryData<string, string> RemindersNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Reminders" },
        { "LifeOS.Application", "LifeOS.Application.Reminders" },
        { "LifeOS.Infrastructure", "LifeOS.Infrastructure.Reminders" },
        { "LifeOS.Contracts", "LifeOS.Contracts.Reminders" }
    };

    [Theory]
    [MemberData(nameof(RemindersNamespaces))]
    public void Reminders_Should_Not_Depend_On_Other_Modules_Ai_Or_Http(string assemblyName, string remindersNamespace)
    {
        var reminderTypes = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(remindersNamespace);

        // Guard against a vacuous pass: the Reminders namespace exists in this assembly.
        Assert.NotEmpty(reminderTypes.GetTypes());

        var result = reminderTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                assemblyName + ".Finance",
                assemblyName + ".Gym",
                assemblyName + ".Nutrition",
                assemblyName + ".Habits",
                assemblyName + ".Journal",
                assemblyName + ".Memory",
                assemblyName + ".WeeklyReviews",
                assemblyName + ".ActionAgent",
                "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void RemindersDomain_IsFrameworkIndependent()
    {
        var result = Types.InAssembly(Assembly.Load("LifeOS.Domain")).That().ResideInNamespace("LifeOS.Domain.Reminders")
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "LifeOS.Application", "LifeOS.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // R3: reminders reach devices only through the automation result (outbox, quiet hours, retries); the
    // module never dispatches, enqueues or opens its own transaction.
    [Fact]
    public void Reminders_NeverDispatchOrEnqueueDirectly()
    {
        var result = Types.InAssembly(Assembly.Load("LifeOS.Application")).That().ResideInNamespace("LifeOS.Application.Reminders")
            .ShouldNot()
            .HaveDependencyOnAny(
                "LifeOS.Application.Notifications.NotificationDispatcher",
                "LifeOS.Application.Notifications.INotificationDeliveryStore",
                "LifeOS.Application.Automation.IAutomationExecutionStore",
                "LifeOS.Application.Automation.RunAutomationTick",
                "LifeOS.Application.Persistence.IUnitOfWork",
                "Microsoft.EntityFrameworkCore", "Npgsql", "LifeOS.Infrastructure", "Google.Apis", "Firebase")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // The API adapter maps to contracts; persistence stays in Infrastructure.
    [Fact]
    public void RemindersApi_Should_Not_Depend_On_Persistence()
    {
        var api = Types.InAssembly(Assembly.Load("LifeOS.Api")).That().ResideInNamespace("LifeOS.Api.Reminders");

        Assert.NotEmpty(api.GetTypes());

        var result = api
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Microsoft.EntityFrameworkCore", "LifeOS.Infrastructure", "Google.Apis")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
