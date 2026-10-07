using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Infrastructure.Finance.Budgets;
using LifeOS.Application.Authentication;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Application.Gym.Training;
using LifeOS.Application.Nutrition;
using LifeOS.Application.Users;
using LifeOS.Infrastructure.Finance.Accounts;
using LifeOS.Infrastructure.Finance.Categories;
using LifeOS.Infrastructure.Finance.Transactions;
using LifeOS.Infrastructure.Gym.Exercises;
using LifeOS.Infrastructure.Gym.Programs;
using LifeOS.Infrastructure.Gym.Sessions;
using LifeOS.Infrastructure.Gym.Training;
using LifeOS.Infrastructure.Nutrition;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeOS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        NutritionAiOptions? nutritionAi = null,
        LifeOS.Infrastructure.Notifications.Fcm.FcmOptions? fcm = null)
    {
        services.AddDbContext<LifeOSDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IMonthlyBudgetRepository, MonthlyBudgetRepository>();
        services.AddScoped<IFinancePlanningSnapshotRepository, FinancePlanningSnapshotRepository>();
        services.AddScoped<LifeOS.Application.Finance.PlannedExpenses.IPlannedExpenseRepository, LifeOS.Infrastructure.Finance.PlannedExpenses.PlannedExpenseRepository>();
        services.AddScoped<LifeOS.Application.Finance.Recurring.IRecurringRepository, LifeOS.Infrastructure.Finance.Recurring.RecurringRepository>();
        services.AddScoped<IAccountReconciliationRepository, AccountReconciliationRepository>();
        services.AddScoped<IAccountBalanceAdjustmentRepository, AccountReconciliationRepository>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IOpeningBalanceRepository, OpeningBalanceRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserSessionRepository, UserSessionRepository>();
        services.AddScoped<IExerciseRepository, ExerciseRepository>();
        services.AddScoped<IWorkoutProgramRepository, WorkoutProgramRepository>();
        services.AddScoped<IWorkoutSessionRepository, WorkoutSessionRepository>();
        services.AddScoped<IActiveProgramRepository, ActiveProgramRepository>();
        services.AddScoped<IMealEntryRepository, MealEntryRepository>();
        services.AddScoped<IMealNutritionRepository, MealNutritionRepository>();
        services.AddScoped<INutritionTargetPlanRepository, NutritionTargetPlanRepository>();
        services.AddScoped<LifeOS.Application.Automation.IAutomationExecutionStore, LifeOS.Infrastructure.Automation.AutomationExecutionStore>();
        services.AddScoped<LifeOS.Application.Notifications.IDeviceRegistrationRepository, LifeOS.Infrastructure.Notifications.DeviceRegistrationRepository>();
        services.AddScoped<LifeOS.Application.Notifications.INotificationDeliveryStore, LifeOS.Infrastructure.Notifications.NotificationDeliveryStore>();
        services.AddScoped<LifeOS.Application.Persistence.IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<LifeOS.Application.WeeklyReviews.IWeeklyReviewRepository, LifeOS.Infrastructure.WeeklyReviews.WeeklyReviewRepository>();
        services.AddScoped<LifeOS.Application.Notifications.INotificationPreferencesRepository, LifeOS.Infrastructure.Notifications.NotificationPreferencesRepository>();
        services.AddScoped<LifeOS.Application.Finance.Reminders.IFinanceReminderRepository, LifeOS.Infrastructure.Finance.Reminders.FinanceReminderRepository>();
        services.AddScoped<LifeOS.Application.Journal.IJournalEntryRepository, LifeOS.Infrastructure.Journal.JournalEntryRepository>();

        // AUTO-001: push only when FCM is configured. Without it no IPushNotificationSender exists, so
        // notification dispatch stays disabled and never marks a delivery.
        if (fcm is not null)
        {
            LifeOS.Infrastructure.Notifications.Fcm.FcmRegistration.AddFcmPushNotifications(services, fcm);
        }

        // One long-lived HttpClient for the Python AI service (ADR-011); none when it is not configured.
        var ai = nutritionAi ?? NutritionAiOptions.Disabled;
        services.AddSingleton<INutritionEstimationService>(provider => new NutritionEstimationClient(
            ai.BaseUrl is null
                ? null
                : new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                {
                    BaseAddress = ai.BaseUrl,
                    Timeout = ai.Timeout
                },
            ai.ServiceKey,
            provider.GetService<ILogger<NutritionEstimationClient>>() ?? NullLogger<NutritionEstimationClient>.Instance));

        return services;
    }
}
