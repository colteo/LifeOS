using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Application.Finance.Budgets;
using System.Security.Cryptography;
using LifeOS.Api.Authentication;
using LifeOS.Application.Authentication;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Application.Gym.Training;
using LifeOS.Application.Users;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.Http;

// Hosts the real API pipeline (routing, JWT validation, authorization) in memory.
// User, session, Finance and Gym persistence use in-memory repositories; PostgreSQL is never contacted.
internal sealed class LifeOSApiFactory : WebApplicationFactory<Program>
{
    private sealed class TestBudgetSnapshot(LifeOS.Application.Finance.Recurring.IRecurringRepository recurring) : IFinancePlanningSnapshotRepository
    {
        public async Task<FinancePlanningSnapshot> ReadAsync(Guid userId, int year, int month, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            var read = await recurring.ReadAsync(userId, year, month, year, month, ct, fromUtc, toUtc);
            return new(read, new([], [], read.Accounts), read.Transactions!);
        }
    }
    private readonly string _environment;
    private readonly bool _developmentSignInEnabled;
    private readonly string _signingKey;
    private readonly string[] _allowedEmails;
    private readonly Action<IWebHostBuilder>? _configure;

    // The Google account allowlist always gets an explicit test address (no real address in tests);
    // pass [] for none.
    public const string AllowedEmail = "person@example.com";

    public LifeOSApiFactory(
        string environment = "Development",
        bool developmentSignInEnabled = true,
        string? signingKey = null,
        string[]? allowedEmails = null,
        Action<IWebHostBuilder>? configure = null)
    {
        _environment = environment;
        _developmentSignInEnabled = developmentSignInEnabled;
        _signingKey = signingKey ?? NewSigningKey();
        _allowedEmails = allowedEmails ?? [AllowedEmail];
        _configure = configure;
        Accounts = new InMemoryAccountRepository(OpeningBalances);
        Reconciliations = new InMemoryAccountReconciliationRepository(Accounts, OpeningBalances, Transactions);
        Recurring = new InMemoryRecurringRepository(Accounts, Categories, Transactions);
        NotificationDeliveries = new InMemoryNotificationDeliveryStore(Devices);
        Accounts.ReconciliationsExist = (owner, account) => Reconciliations.Receipts.Any(r => r.UserId == owner && r.AccountId == account);
    }

    public InMemoryUserRepository Users { get; } = new();

    public InMemoryUserSessionRepository Sessions { get; } = new();

    public InMemoryAccountRepository Accounts { get; }

    public InMemoryOpeningBalanceRepository OpeningBalances { get; } = new();

    public InMemoryCategoryRepository Categories { get; } = new();

    public InMemoryAccountReconciliationRepository Reconciliations { get; }

    public InMemoryMonthlyBudgetRepository Budgets { get; } = new();
    public InMemoryRecurringRepository Recurring { get; }

    public InMemoryTransactionRepository Transactions { get; } = new();

    public InMemoryExerciseRepository Exercises { get; } = new();

    public InMemoryWorkoutProgramRepository WorkoutPrograms { get; } = new();

    public InMemoryWorkoutSessionRepository WorkoutSessions { get; } = new();

    public InMemoryActiveProgramRepository ActivePrograms { get; } = new();

    public InMemoryAutomationExecutionStore AutomationExecutions { get; } = new();

    public InMemoryDeviceRegistrationRepository Devices { get; } = new();

    public InMemoryNotificationDeliveryStore NotificationDeliveries { get; }

    // The host's TimeProvider: real time plus an adjustable offset (e.g. to expire authorization codes).
    public AdjustableTimeProvider Clock { get; } = new();

    public static string NewSigningKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public LifeOSTokenOptions TokenOptions => Services.GetRequiredService<LifeOSTokenOptions>();

    // A valid access token for any user id, issued by the host's own issuer.
    public string IssueAccessToken(Guid userId) => Services.GetRequiredService<AccessTokenIssuer>().Issue(userId);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);

        // Explicit values, so local User Secrets cannot change the outcome of a test.
        builder.UseSetting("ConnectionStrings:PostgreSQL", "Host=unused.invalid;Database=unused");
        builder.UseSetting("Authentication:LifeOS:SigningKey", _signingKey);
        builder.UseSetting(DevelopmentSignIn.EnabledKey, _developmentSignInEnabled ? "true" : "false");

        // AI estimation disabled unless a test configures it (PROD-AI-001: a base URL needs a service key).
        builder.UseSetting("NutritionAi:BaseUrl", "");

        // AUTO-001: automation (the tick endpoint) disabled unless a test configures a key.
        builder.UseSetting("Automation:TickKey", "");

        // AUTO-001: push (FCM) disabled unless a test configures it.
        builder.UseSetting("Notifications:Fcm:ProjectId", "");
        builder.UseSetting("Notifications:Fcm:ServiceAccountJson", "");

        // Dummy Google client: registers the Google handler without contacting Google.
        builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
        builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");

        for (var index = 0; index < _allowedEmails.Length; index++)
        {
            builder.UseSetting($"{GoogleAccountAllowlist.AllowedEmailsKey}:{index}", _allowedEmails[index]);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IUserRepository>(Users);
            services.AddSingleton<IUserSessionRepository>(Sessions);
            services.AddSingleton<IAccountRepository>(Accounts);
            services.AddSingleton<IOpeningBalanceRepository>(OpeningBalances);
            services.AddSingleton<ICategoryRepository>(Categories);
            services.AddSingleton<ITransactionRepository>(Transactions);
            services.AddSingleton<IMonthlyBudgetRepository>(Budgets);
            services.AddSingleton<IFinancePlanningSnapshotRepository>(new TestBudgetSnapshot(Recurring));
            services.AddSingleton<LifeOS.Application.Finance.Recurring.IRecurringRepository>(Recurring);
            services.AddSingleton<IAccountReconciliationRepository>(Reconciliations);
            services.AddSingleton<IAccountBalanceAdjustmentRepository>(Reconciliations);
            services.AddSingleton<IExerciseRepository>(Exercises);
            services.AddSingleton<IWorkoutProgramRepository>(WorkoutPrograms);
            services.AddSingleton<IWorkoutSessionRepository>(WorkoutSessions);
            services.AddSingleton<IActiveProgramRepository>(ActivePrograms);
            services.AddSingleton<LifeOS.Application.Automation.IAutomationExecutionStore>(AutomationExecutions);
            services.AddSingleton<LifeOS.Application.Notifications.IDeviceRegistrationRepository>(Devices);
            services.AddSingleton<LifeOS.Application.Notifications.INotificationDeliveryStore>(NotificationDeliveries);
            services.AddSingleton<LifeOS.Application.Persistence.IUnitOfWork>(new FakeUnitOfWork());
            services.AddSingleton<TimeProvider>(Clock);
        });

        _configure?.Invoke(builder);
    }
}

internal sealed class AdjustableTimeProvider : TimeProvider
{
    private TimeSpan _offset;

    public void Advance(TimeSpan by) => _offset += by;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;
}
