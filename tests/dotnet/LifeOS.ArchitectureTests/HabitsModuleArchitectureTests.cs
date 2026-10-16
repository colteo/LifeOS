using System.Reflection;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// HAB-001 §11-12: the Habits module depends on no other business module except Hydration, which its
// Application layer reads only through IHydrationDayReader (B4); never on Reminders, automation,
// notifications or AI, and has no HTTP client. Habits never stores water amounts.
public class HabitsModuleArchitectureTests
{
    public static TheoryData<string, string> HabitsNamespaces => new()
    {
        { "LifeOS.Domain", "LifeOS.Domain.Habits" },
        { "LifeOS.Application", "LifeOS.Application.Habits" },
        { "LifeOS.Infrastructure", "LifeOS.Infrastructure.Habits" },
        { "LifeOS.Contracts", "LifeOS.Contracts.Habits" }
    };

    [Theory]
    [MemberData(nameof(HabitsNamespaces))]
    public void Habits_Should_Not_Depend_On_Other_Modules_Ai_Or_Http(string assemblyName, string habitsNamespace)
    {
        var habitTypes = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(habitsNamespace);

        // Guard against a vacuous pass: the Habits namespace exists in this assembly.
        Assert.NotEmpty(habitTypes.GetTypes());

        var result = habitTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                assemblyName + ".Finance",
                assemblyName + ".Gym",
                // Application.Habits → Hydration is checked type by type below.
                assemblyName == "LifeOS.Application" ? "LifeOS.Application.Nutrition.NoTypeIsNamedThis" : assemblyName + ".Nutrition",
                assemblyName + ".Journal",
                assemblyName + ".Memory",
                assemblyName + ".WeeklyReviews",
                assemblyName + ".ActionAgent",
                assemblyName + ".Automation",
                assemblyName + ".Notifications",
                assemblyName + ".Reminders",
                "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // B4: the water habit reads Hydration only through its read port (and the day value it returns).
    [Fact]
    public void HabitsApplication_ReadsHydration_OnlyThroughTheDayReader()
    {
        string[] allowed = ["LifeOS.Application.Nutrition.Hydration.IHydrationDayReader", "LifeOS.Domain.Nutrition.Hydration.HydrationDay"];
        var forbidden = new[] { ("LifeOS.Application", "LifeOS.Application.Nutrition"), ("LifeOS.Domain", "LifeOS.Domain.Nutrition") }
            .SelectMany(pair => Types.InAssembly(Assembly.Load(pair.Item1)).That().ResideInNamespace(pair.Item2).GetTypes())
            .Select(type => type.FullName!)
            .Where(name => !allowed.Contains(name))
            .ToArray();
        var habits = Types.InAssembly(Assembly.Load("LifeOS.Application")).That().ResideInNamespace("LifeOS.Application.Habits");

        Assert.NotEmpty(forbidden);
        // Guard against a vacuous pass: the handlers do take the reader.
        Assert.Contains(habits.GetTypes(), type => type.GetConstructors().Any(constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType.FullName == allowed[0])));

        var result = habits.ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void HabitsDomain_HasNoWaterAmount()
    {
        var members = Types.InAssembly(Assembly.Load("LifeOS.Domain")).That().ResideInNamespace("LifeOS.Domain.Habits").GetTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(member => member.Name)
            .Where(name => name.EndsWith("Ml", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(members);
    }

    [Fact]
    public void HabitsDomain_IsFrameworkIndependent()
    {
        var result = Types.InAssembly(Assembly.Load("LifeOS.Domain")).That().ResideInNamespace("LifeOS.Domain.Habits")
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "LifeOS.Application", "LifeOS.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
