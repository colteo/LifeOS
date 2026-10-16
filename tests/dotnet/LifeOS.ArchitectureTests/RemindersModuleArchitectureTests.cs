using System.Reflection;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// REM-001 §13: the Reminders module depends on no other business module except Habits and Hydration,
// which its Application layer (R5) reads only through their reader ports (IHabitDayStatusReader,
// IHydrationDayReader); it has no HTTP or AI client, and its Domain is framework independent. It plugs
// into the automation and notification core, never the other way round (AutomationArchitectureTests).
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

        // Application.Reminders → Habits/Hydration is checked type by type below.
        var readsPorts = assemblyName == "LifeOS.Application";
        var result = reminderTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                assemblyName + ".Finance",
                assemblyName + ".Gym",
                readsPorts ? "LifeOS.Application.Nutrition.NoTypeIsNamedThis" : assemblyName + ".Nutrition",
                readsPorts ? "LifeOS.Application.Habits.NoTypeIsNamedThis" : assemblyName + ".Habits",
                assemblyName + ".Journal",
                assemblyName + ".Memory",
                assemblyName + ".WeeklyReviews",
                assemblyName + ".ActionAgent",
                "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // R5: the conditions read Habits and Hydration only through their read ports (and the values they return).
    [Fact]
    public void RemindersApplication_ReadsHabitsAndHydration_OnlyThroughTheirReaders()
    {
        string[] allowed =
        [
            "LifeOS.Application.Habits.IHabitDayStatusReader",
            "LifeOS.Application.Habits.HabitDayStatusSnapshot",
            "LifeOS.Domain.Habits.HabitDayStatus",
            "LifeOS.Application.Nutrition.Hydration.IHydrationDayReader",
            "LifeOS.Domain.Nutrition.Hydration.HydrationDay"
        ];
        var forbidden = new[]
            {
                ("LifeOS.Application", "LifeOS.Application.Habits"), ("LifeOS.Domain", "LifeOS.Domain.Habits"),
                ("LifeOS.Application", "LifeOS.Application.Nutrition"), ("LifeOS.Domain", "LifeOS.Domain.Nutrition")
            }
            .SelectMany(pair => Types.InAssembly(Assembly.Load(pair.Item1)).That().ResideInNamespace(pair.Item2).GetTypes())
            .Select(type => type.FullName!)
            .Where(name => !allowed.Contains(name))
            .ToArray();
        var reminders = Types.InAssembly(Assembly.Load("LifeOS.Application")).That().ResideInNamespace("LifeOS.Application.Reminders");

        Assert.NotEmpty(forbidden);
        // Guard against a vacuous pass: the evaluator does take both readers.
        Assert.Contains(reminders.GetTypes(), type => type.GetConstructors().Any(constructor =>
            constructor.GetParameters().Select(parameter => parameter.ParameterType.FullName).ToHashSet().IsSupersetOf([allowed[0], allowed[3]])));

        var result = reminders.ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // The relevance check is the only module code the notification core runs, and Reminders' is the only one.
    [Fact]
    public void TheOnlyRelevanceCheck_IsTheRemindersOne()
    {
        var implementations = new[] { "LifeOS.Domain", "LifeOS.Application", "LifeOS.Infrastructure", "LifeOS.Api" }
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That()
                .ImplementInterface(typeof(LifeOS.Application.Notifications.INotificationRelevanceCheck)).GetTypes())
            .Select(type => type.FullName);

        Assert.Equal(["LifeOS.Application.Reminders.ReminderRelevanceCheck"], implementations);
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
