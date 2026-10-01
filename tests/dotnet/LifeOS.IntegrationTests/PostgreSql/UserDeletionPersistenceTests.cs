using LifeOS.Application.Authentication;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// Current foreign-key delete semantics only (no account-deletion feature exists).
// ON DELETE RESTRICT is reported by PostgreSQL as 23001 restrict_violation (NO ACTION would be 23503).
// Deletes run in the database (ExecuteDeleteAsync), so the tests observe PostgreSQL's FK
// behaviour rather than EF Core's client-side cascade.
[Collection(PostgreSqlCollection.Name)]
public class UserDeletionPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UserWithAccount_CannotBeDeleted()
    {
        var user = await NewUserAsync();
        await PostgresAssert.InsertAsync(fixture, Account.Create(user.Id, "Checking", AccountType.BankAccount, "EUR", Now));

        await PostgresAssert.DeleteBlockedAsync(
            "FK_accounts_users_user_id", () => DeleteUserAsync(user.Id));
    }

    [Fact]
    public async Task UserWithCategory_CannotBeDeleted()
    {
        var user = await NewUserAsync();
        await PostgresAssert.InsertAsync(fixture, Category.Create(user.Id, "Casa", CategoryType.Expense, parent: null, Now));

        await PostgresAssert.DeleteBlockedAsync(
            "FK_categories_users_user_id", () => DeleteUserAsync(user.Id));
    }

    [Fact]
    public async Task UserWithTransaction_CannotBeDeleted()
    {
        var user = await NewUserAsync();
        var account = Account.Create(user.Id, "Checking", AccountType.BankAccount, "EUR", Now);
        var category = Category.Create(user.Id, "Casa", CategoryType.Expense, parent: null, Now);
        var expense = Transaction.CreateExpense(user.Id, account.Id, category.Id, 10m, "EUR", Now, null, Now);
        await PostgresAssert.InsertAsync(fixture, account, category, expense);

        // Accounts, categories and transactions all reference the user with RESTRICT; PostgreSQL does
        // not define which one fires first, but one of them must block the delete.
        await PostgresAssert.DeleteBlockedByOneOfAsync(
            ["FK_accounts_users_user_id", "FK_categories_users_user_id", "FK_transactions_users_user_id"],
            () => DeleteUserAsync(user.Id));

        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        Assert.True(await dbContext.Users.AnyAsync(stored => stored.Id == user.Id));
        Assert.True(await dbContext.Transactions.AnyAsync(stored => stored.Id == expense.Id));
    }

    [Fact]
    public async Task UserWithoutFinanceData_IsDeletedTogetherWithIdentitiesAndSessions()
    {
        var user = await NewUserAsync();
        var identity = ExternalIdentity.Create(user.Id, "dev", "subject-" + Guid.NewGuid().ToString("N"), null, Now);
        var session = UserSession.Start(user.Id, RefreshTokens.Hash(RefreshTokens.Generate()), Now, TimeSpan.FromDays(30));
        await PostgresAssert.InsertAsync(fixture, identity, session);

        Assert.Equal(1, await DeleteUserAsync(user.Id));

        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        Assert.False(await dbContext.ExternalIdentities.AnyAsync(stored => stored.Id == identity.Id));
        Assert.False(await dbContext.UserSessions.AnyAsync(stored => stored.Id == session.Id));
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task<int> DeleteUserAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Users
            .Where(user => user.Id == userId)
            .ExecuteDeleteAsync();
    }
}
