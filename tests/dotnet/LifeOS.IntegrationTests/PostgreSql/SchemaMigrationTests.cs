using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LifeOS.IntegrationTests.PostgreSql;

// The fixture builds a fresh database from the real migration chain on whatever server image it runs
// (the pinned default or LIFEOS_POSTGRES_IMAGE). These checks prove the chain applied completely on
// that server and that the PostgreSQL-specific schema features exist; the behaviour of those features
// is covered by the persistence tests.
[Collection(PostgreSqlCollection.Name)]
public class SchemaMigrationTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task EveryMigration_IsApplied_AndTheModelMatchesTheSnapshot()
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

        var applied = (await database.GetAppliedMigrationsAsync()).ToList();
        output.WriteLine($"PostgreSQL image: {PostgreSqlFixture.Image}");
        output.WriteLine($"PostgreSQL server_version: {fixture.ServerVersion}");
        output.WriteLine($"Applied migrations: {string.Join(", ", applied)}");

        Assert.Equal(database.GetMigrations(), applied);
        Assert.Contains(applied, id => id.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Contains(applied, id => id.EndsWith("_AddCategorySiblingUniqueness", StringComparison.Ordinal));
        Assert.Contains(applied, id => id.EndsWith("_AddOpeningBalances", StringComparison.Ordinal));
        Assert.Contains(applied, id => id.EndsWith("_AddMonthlyBudgets", StringComparison.Ordinal));
        Assert.Contains(applied, id => id.EndsWith("_AddAccountBalanceReconciliation", StringComparison.Ordinal));
        Assert.Contains(applied, id => id.EndsWith("_AddMonthlyRecurringTransactions", StringComparison.Ordinal));
        Assert.True(applied.FindIndex(id => id.EndsWith("_AddMonthlyBudgets", StringComparison.Ordinal)) < applied.FindIndex(id => id.EndsWith("_AddAccountBalanceReconciliation", StringComparison.Ordinal)));
        Assert.Contains(applied, id => id.EndsWith("_AddGymPrograms", StringComparison.Ordinal));
        Assert.Contains(applied, id => id.EndsWith("_AddGymWorkoutSessions", StringComparison.Ordinal));
        // Gym was regenerated on top of Finance: it follows AddMonthlyBudgets, and the unpublished
        // pre-Finance Gym migration is gone.
        Assert.True(
            applied.FindIndex(id => id.EndsWith("_AddMonthlyBudgets", StringComparison.Ordinal))
            < applied.FindIndex(id => id.EndsWith("_AddGymPrograms", StringComparison.Ordinal)));
        Assert.DoesNotContain("20261002064045_AddGymPrograms", applied);
        Assert.True(applied.FindIndex(id => id.EndsWith("_AddGymPrograms", StringComparison.Ordinal))
            < applied.FindIndex(id => id.EndsWith("_AddAccountBalanceReconciliation", StringComparison.Ordinal)));
        Assert.DoesNotContain("20261002091023_AddAccountBalanceReconciliation", applied);
        // Workout execution was regenerated on top of FIN-003: it follows AddAccountBalanceReconciliation
        // (and therefore AddGymPrograms, whose tables its source references point at), and the
        // unpublished pre-FIN-003 migration is gone.
        Assert.True(applied.FindIndex(id => id.EndsWith("_AddAccountBalanceReconciliation", StringComparison.Ordinal))
            < applied.FindIndex(id => id.EndsWith("_AddGymWorkoutSessions", StringComparison.Ordinal)));
        Assert.DoesNotContain("20261002093445_AddGymWorkoutSessions", applied);
        Assert.Empty(await database.GetPendingMigrationsAsync());
        Assert.False(database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Server_MeetsTheMinimumMajor()
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

        var versionNumber = await Scalar<int>(database, "SELECT current_setting('server_version_num')::int AS \"Value\"");

        // NULLS NOT DISTINCT (category sibling uniqueness) needs PostgreSQL 15.
        Assert.True(versionNumber >= 150000, $"PostgreSQL 15+ is required; the server is {fixture.ServerVersion}.");
    }

    [Fact]
    public async Task PostgreSqlSpecificSchemaFeatures_Exist()
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

        var siblingIndex = await Scalar<string>(database,
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_categories_user_sibling_name'");
        Assert.Contains("NULLS NOT DISTINCT", siblingIndex);
        Assert.Contains("lower(name)", siblingIndex);

        var amountTypes = await Strings(database,
            """
            SELECT table_name || '.' || column_name || ' ' || data_type || '(' || numeric_precision || ',' || numeric_scale || ')' AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name = 'amount'
            ORDER BY 1
            """);
        Assert.Equal(["account_balance_adjustments.amount numeric(19,4)", "monthly_budgets.amount numeric(19,4)", "opening_balances.amount numeric(19,4)", "recurring_transaction_rules.amount numeric(19,4)", "transactions.amount numeric(19,4)"], amountTypes);

        var timestampTypes = await Strings(database,
            """
            SELECT DISTINCT data_type AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name LIKE '%\_at\_utc' ESCAPE '\'
            """);
        Assert.Equal(["timestamp with time zone"], timestampTypes);

        // Microsecond precision is kept (no rounding to milliseconds).
        var roundTrip = await Scalar<string>(database,
            "SELECT to_char('2026-09-30 10:00:00.123456+00'::timestamptz AT TIME ZONE 'UTC', 'HH24:MI:SS.US') AS \"Value\"");
        Assert.Equal("10:00:00.123456", roundTrip);

        // Composite (id, user_id) foreign keys, e.g. transactions → accounts of the same user.
        var compositeForeignKeys = await Scalar<int>(database,
            """
            SELECT count(*)::int AS "Value"
            FROM pg_constraint
            WHERE contype = 'f' AND array_length(conkey, 1) = 2 AND connamespace = 'public'::regnamespace
            """);
        Assert.True(compositeForeignKeys > 0);

        var shape = await Scalar<int>(database,
            "SELECT count(*)::int AS \"Value\" FROM pg_constraint WHERE conname = 'ck_transactions_shape' AND contype = 'c'");
        Assert.Equal(1, shape);

        // Gym: case-insensitive exercise names per user, and sibling positions unique per parent,
        // checked at commit (hand-written migration SQL, not part of the EF model).
        var exerciseNameIndex = await Scalar<string>(database,
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_exercises_user_name'");
        Assert.Contains("UNIQUE", exerciseNameIndex);
        Assert.Contains("lower(name)", exerciseNameIndex);

        var deferredPositions = await Strings(database,
            """
            SELECT conname AS "Value"
            FROM pg_constraint
            WHERE contype = 'u' AND condeferrable AND condeferred AND connamespace = 'public'::regnamespace
            ORDER BY 1
            """);
        Assert.Equal(
            [
                "ux_workout_block_exercises_block_position",
                "ux_workout_blocks_template_position",
                "ux_workout_set_prescriptions_exercise_position",
                "ux_workout_templates_program_position"
            ],
            deferredPositions);
    }

    private static Task<T> Scalar<T>(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<T>(sql).SingleAsync();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}

public class PostgreSqlImageSelectionTests
{
    [Fact]
    public void WithoutTheVariable_TheDefaultImageIsUsed() =>
        Assert.Equal("pgvector/pgvector:0.8.6-pg18-trixie", PostgreSqlFixture.ResolveImage(null));

    [Theory]
    [InlineData("postgres:17", "postgres:17")]
    [InlineData("  postgres:17.6  ", "postgres:17.6")]
    public void TheVariable_SelectsTheImage(string value, string expected) =>
        Assert.Equal(expected, PostgreSqlFixture.ResolveImage(value));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("postgres 17")]
    public void ABlankOrMalformedValue_FailsWithTheVariableName(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PostgreSqlFixture.ResolveImage(value));

        Assert.Contains(PostgreSqlFixture.ImageVariable, exception.Message);
    }
}
