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

    // WP3B: exactly one push provider, the FCM HTTP v1 sender, in Infrastructure.
    [Fact]
    public void The_Only_PushNotificationSender_Is_The_Fcm_Sender_In_Infrastructure()
    {
        var senders = ProductionAssemblies
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(IPushNotificationSender)).GetTypes())
            .Select(type => type.FullName);

        Assert.Equal(["LifeOS.Infrastructure.Notifications.Fcm.FcmPushNotificationSender"], senders);
    }

    // Phase A is part of the automation core and depends only on the notification core.
    [Fact]
    public void Tick_Uses_The_Notification_Core_Only_Through_Application_Types()
    {
        var tick = typeof(RunAutomationTick);

        Assert.Contains(tick.GetConstructors().Single().GetParameters(), parameter => parameter.ParameterType == typeof(NotificationDispatcher));
        Assert.Equal("LifeOS.Application.Notifications", typeof(NotificationDispatcher).Namespace);
    }

    // The app's Firebase code is Android platform code only; shared app code stays provider-neutral
    // (IPushPlatform). The only Firebase package is the Android FCM client binding.
    [Fact]
    public void App_Firebase_Code_Is_Android_Platform_Code_Only()
    {
        var app = AppDirectory();
        var android = Path.Combine(app, "Platforms", "Android") + Path.DirectorySeparatorChar;

        Assert.True(Directory.Exists(app), app);

        var firebaseFiles = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file) && FirebaseCode().IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.NotEmpty(firebaseFiles);
        Assert.All(firebaseFiles, file => Assert.StartsWith(android, file, StringComparison.Ordinal));

        var project = File.ReadAllText(Path.Combine(app, "LifeOS.App.csproj"));
        Assert.Contains("<PackageReference Include=\"Xamarin.Firebase.Messaging\"", project);
        Assert.DoesNotContain("FirebaseAdmin", project);
    }

    // A Firebase namespace in code (a using or a qualified name), not the word in a comment.
    private static System.Text.RegularExpressions.Regex FirebaseCode() => new(@"\busing\s+Firebase\b|\bFirebase\.[A-Z]");

    private static string AppDirectory([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "dotnet", "LifeOS.App"));

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // The tick, device and notification endpoints are adapters: transport and authentication, no
    // persistence and no provider SDK. (Reading the FCM options into Infrastructure's FcmOptions is
    // composition, like NutritionAiConfiguration.)
    [Theory]
    [InlineData("LifeOS.Api.Automation")]
    [InlineData("LifeOS.Api.Notifications")]
    public void ApiAdapters_Should_Not_Depend_On_Persistence(string apiNamespace)
    {
        var api = Types.InAssembly(Assembly.Load("LifeOS.Api")).That().ResideInNamespace(apiNamespace);

        Assert.NotEmpty(api.GetTypes());

        var result = api
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Microsoft.EntityFrameworkCore", "LifeOS.Infrastructure.Persistence", "LifeOS.Domain", "Google.Apis")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // AUTO-002: the Weekly Review endpoints map the Domain snapshot to contracts (like the module
    // endpoints) but never touch Infrastructure, persistence or a provider.
    [Fact]
    public void WeeklyReviewApi_Should_Not_Depend_On_Persistence()
    {
        var api = Types.InAssembly(Assembly.Load("LifeOS.Api")).That().ResideInNamespace("LifeOS.Api.WeeklyReviews");

        Assert.NotEmpty(api.GetTypes());

        var result = api
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Microsoft.EntityFrameworkCore", "LifeOS.Infrastructure", "Google.Apis")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // AUTO-001 shipped zero business handlers; AUTO-002 adds exactly one, in its own Application module.
    [Fact]
    public void The_Only_Production_AutomationHandler_Is_The_WeeklyReview_Handler()
    {
        var handlers = ProductionAssemblies
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That().ImplementInterface(typeof(IAutomationHandler)).GetTypes())
            .Select(type => type.FullName);

        Assert.Equal(["LifeOS.Application.WeeklyReviews.WeeklyReviewAutomationHandler"], handlers);
    }

    // AUTO-002: the handler depends on the core; the core never depends on the Weekly Review module.
    [Theory]
    [MemberData(nameof(CoreNamespaces))]
    public void AutomationCore_Should_Not_Depend_On_WeeklyReviews(string assemblyName, string automationNamespace)
    {
        var result = Types.InAssembly(Assembly.Load(assemblyName))
            .That().ResideInNamespace(automationNamespace)
            .ShouldNot()
            .HaveDependencyOnAny("LifeOS.Domain.WeeklyReviews", "LifeOS.Application.WeeklyReviews")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // AUTO-002: Weekly Review Domain/Application stay free of persistence, hosting, push providers and AI.
    [Theory]
    [InlineData("LifeOS.Domain", "LifeOS.Domain.WeeklyReviews")]
    [InlineData("LifeOS.Application", "LifeOS.Application.WeeklyReviews")]
    public void WeeklyReviews_Should_Not_Depend_On_Persistence_Hosting_Or_Providers(string assemblyName, string moduleNamespace)
    {
        var module = Types.InAssembly(Assembly.Load(assemblyName)).That().ResideInNamespace(moduleNamespace);

        Assert.NotEmpty(module.GetTypes());

        var result = module
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "LifeOS.Infrastructure", "LifeOS.Api",
                "Google.Apis", "Firebase", "System.Text.Json", "LifeOS.Application.Nutrition.INutritionEstimationService")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // AUTO-002 W-4: generation is read-only. The module never reaches the estimator or the Nutrition
    // write/lazy-close use cases.
    [Fact]
    public void WeeklyReviews_Never_Uses_Nutrition_Estimation_Or_LazyClose()
    {
        var result = Types.InAssembly(Assembly.Load("LifeOS.Application"))
            .That().ResideInNamespace("LifeOS.Application.WeeklyReviews")
            .ShouldNot()
            .HaveDependencyOnAny(
                "LifeOS.Application.Nutrition.LazyCloseNutritionHandler",
                "LifeOS.Application.Nutrition.MealNutritionEstimation",
                "LifeOS.Application.Nutrition.AnalyzeDayHandler",
                "LifeOS.Application.Nutrition.INutritionEstimationService")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void WeeklyReviewRepository_Is_Implemented_Only_In_Infrastructure()
    {
        var implementations = ProductionAssemblies
            .SelectMany(name => Types.InAssembly(Assembly.Load(name)).That()
                .ImplementInterface(typeof(LifeOS.Application.WeeklyReviews.IWeeklyReviewRepository)).GetTypes())
            .Select(type => type.FullName);

        Assert.Equal(["LifeOS.Infrastructure.WeeklyReviews.WeeklyReviewRepository"], implementations);
    }

    // PD-6: FCM HTTP v1 + Google.Apis.Auth, only in Infrastructure; never the Firebase Admin SDK.
    [Theory]
    [InlineData("LifeOS.Domain")]
    [InlineData("LifeOS.Application")]
    [InlineData("LifeOS.Contracts")]
    [InlineData("LifeOS.Api")]
    public void Google_And_Firebase_Types_Stay_Out_Of_Everything_But_Infrastructure(string assemblyName)
    {
        string[] providerNamespaces = ["Google.Apis", "FirebaseAdmin", "Firebase"];
        var assembly = Assembly.Load(assemblyName);

        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            providerNamespaces.Any(provider => reference.Name!.StartsWith(provider, StringComparison.Ordinal)));

        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(providerNamespaces).GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Infrastructure_Uses_Google_Apis_Auth_Only_For_Fcm_And_Never_The_Admin_Sdk()
    {
        var infrastructure = Assembly.Load("LifeOS.Infrastructure");

        Assert.DoesNotContain(infrastructure.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("FirebaseAdmin", StringComparison.Ordinal));

        var result = Types.InAssembly(infrastructure)
            .That().HaveDependencyOn("Google.Apis")
            .Should().ResideInNamespace("LifeOS.Infrastructure.Notifications.Fcm")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
