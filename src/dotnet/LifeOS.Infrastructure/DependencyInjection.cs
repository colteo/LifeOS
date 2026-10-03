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
using LifeOS.Application.Users;
using LifeOS.Infrastructure.Finance.Accounts;
using LifeOS.Infrastructure.Finance.Categories;
using LifeOS.Infrastructure.Finance.Transactions;
using LifeOS.Infrastructure.Gym.Exercises;
using LifeOS.Infrastructure.Gym.Programs;
using LifeOS.Infrastructure.Gym.Sessions;
using LifeOS.Infrastructure.Gym.Training;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
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

        return services;
    }
}
