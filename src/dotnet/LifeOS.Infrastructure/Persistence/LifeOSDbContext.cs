using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
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

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<OpeningBalance> OpeningBalances => Set<OpeningBalance>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<User> Users => Set<User>();

    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LifeOSDbContext).Assembly);
    }
}
