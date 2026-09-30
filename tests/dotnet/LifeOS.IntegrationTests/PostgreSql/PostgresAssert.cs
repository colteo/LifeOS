using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

internal static class PostgresAssert
{
    public const string UniqueViolation = PostgresErrorCodes.UniqueViolation;         // 23505
    public const string ForeignKeyViolation = PostgresErrorCodes.ForeignKeyViolation; // 23503
    public const string RestrictViolation = PostgresErrorCodes.RestrictViolation;     // 23001

    // Asserts that the operation fails because PostgreSQL rejected it with exactly this error
    // code on exactly this constraint.
    public static async Task ViolatesAsync(string sqlState, string constraintName, Func<Task> operation)
    {
        var postgres = await ThrowsPostgresAsync(operation);

        Assert.Equal(sqlState, postgres.SqlState);
        Assert.Equal(constraintName, postgres.ConstraintName);
    }

    // For operations that several constraints can reject, where PostgreSQL does not define which fires first.
    public static async Task ViolatesOneOfAsync(string sqlState, IReadOnlyCollection<string> constraintNames, Func<Task> operation)
    {
        var postgres = await ThrowsPostgresAsync(operation);

        Assert.Equal(sqlState, postgres.SqlState);
        Assert.Contains(postgres.ConstraintName, constraintNames);
    }

    private static async Task<PostgresException> ThrowsPostgresAsync(Func<Task> operation)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(operation);

        var postgres = FindPostgresException(exception);
        Assert.True(postgres is not null, $"Expected a PostgresException but got: {exception}");

        return postgres!;
    }

    // Saves the entities in a fresh scope and asserts the database rejects them.
    public static Task InsertViolatesAsync(
        PostgreSqlFixture fixture,
        string sqlState,
        string constraintName,
        params object[] entities) =>
        ViolatesAsync(sqlState, constraintName, () => InsertAsync(fixture, entities));

    public static async Task InsertAsync(PostgreSqlFixture fixture, params object[] entities)
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}

internal static class DbContextExtensions
{
    public static int PendingInserts<TEntity>(this DbContext dbContext) where TEntity : class =>
        dbContext.ChangeTracker.Entries<TEntity>().Count(entry => entry.State == EntityState.Added);
}
