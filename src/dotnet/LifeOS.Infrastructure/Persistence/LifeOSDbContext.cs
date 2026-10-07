using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Gym.Training;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Persistence;

public sealed class LifeOSDbContext : DbContext
{
    public LifeOSDbContext(DbContextOptions<LifeOSDbContext> options)
        : base(options)
    {
    }

    public DbSet<LifeOS.Domain.Finance.Budgets.MonthlyBudget> MonthlyBudgets => Set<LifeOS.Domain.Finance.Budgets.MonthlyBudget>();

    public DbSet<AccountBalanceAdjustment> AccountBalanceAdjustments => Set<AccountBalanceAdjustment>();

    public DbSet<AccountReconciliation> AccountReconciliations => Set<AccountReconciliation>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<OpeningBalance> OpeningBalances => Set<OpeningBalance>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<User> Users => Set<User>();

    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<Exercise> Exercises => Set<Exercise>();

    public DbSet<WorkoutProgram> WorkoutPrograms => Set<WorkoutProgram>();

    public DbSet<WorkoutSession> WorkoutSessions => Set<WorkoutSession>();

    public DbSet<ActiveProgram> ActivePrograms => Set<ActiveProgram>();

    public DbSet<MealEntry> MealEntries => Set<MealEntry>();

    public DbSet<MealNutritionSnapshot> MealNutritionSnapshots => Set<MealNutritionSnapshot>();

    public DbSet<NutritionTargetPlan> NutritionTargetPlans => Set<NutritionTargetPlan>();

    public DbSet<NutritionTargetOverride> NutritionTargetOverrides => Set<NutritionTargetOverride>();

    public DbSet<LifeOS.Domain.Automation.AutomationExecution> AutomationExecutions => Set<LifeOS.Domain.Automation.AutomationExecution>();

    public DbSet<LifeOS.Domain.Notifications.DeviceRegistration> DeviceRegistrations => Set<LifeOS.Domain.Notifications.DeviceRegistration>();

    public DbSet<LifeOS.Domain.Notifications.NotificationDelivery> NotificationDeliveries => Set<LifeOS.Domain.Notifications.NotificationDelivery>();

    internal DbSet<LifeOS.Infrastructure.WeeklyReviews.WeeklyReviewRecord> WeeklyReviews => Set<LifeOS.Infrastructure.WeeklyReviews.WeeklyReviewRecord>();

    internal DbSet<LifeOS.Infrastructure.WeeklyReviews.WeeklyReviewInsightsRecord> WeeklyReviewInsights => Set<LifeOS.Infrastructure.WeeklyReviews.WeeklyReviewInsightsRecord>();

    public DbSet<LifeOS.Domain.WeeklyReviews.WeeklyReviewSettings> WeeklyReviewSettings => Set<LifeOS.Domain.WeeklyReviews.WeeklyReviewSettings>();

    public DbSet<LifeOS.Domain.Notifications.NotificationPreferences> NotificationPreferences => Set<LifeOS.Domain.Notifications.NotificationPreferences>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LifeOSDbContext).Assembly);
    }
}
