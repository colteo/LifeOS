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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LifeOSDbContext).Assembly);
    }
}
