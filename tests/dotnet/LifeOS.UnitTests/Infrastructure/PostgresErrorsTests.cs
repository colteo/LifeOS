using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LifeOS.UnitTests.Infrastructure;

// Expected PostgreSQL errors are recognized by SQLSTATE AND exact constraint name; everything else
// propagates. A blocked DELETE is 23001 on PostgreSQL 18 and 23503 on 15-17.
public class PostgresErrorsTests
{
    private const string Restricting = "FK_transactions_accounts_account_id_user_id";
    private const string Other = "FK_categories_users_user_id";

    [Theory]
    [InlineData(PostgresErrorCodes.RestrictViolation)]   // PostgreSQL 18
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)] // PostgreSQL 15-17
    public void DeleteBlockedByReference_IsRecognized_OnEverySupportedMajor(string sqlState)
    {
        Assert.True(PostgresErrors.IsDeleteBlockedByReference(Error(sqlState, Restricting), Restricting));
        // ExecuteDelete throws the PostgresException itself; SaveChanges wraps it.
        Assert.True(PostgresErrors.IsDeleteBlockedByReference(new DbUpdateException("x", Error(sqlState, Restricting)), Restricting));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.RestrictViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    public void DeleteBlockedByReference_OnAnotherConstraint_IsNotRecognized(string sqlState)
    {
        Assert.False(PostgresErrors.IsDeleteBlockedByReference(Error(sqlState, Other), Restricting));
        Assert.False(PostgresErrors.IsDeleteBlockedByReference(Error(sqlState, constraintName: null), Restricting));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData(PostgresErrorCodes.NotNullViolation)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public void OtherErrors_OnTheRestrictingConstraint_AreNotRecognized(string sqlState) =>
        Assert.False(PostgresErrors.IsDeleteBlockedByReference(Error(sqlState, Restricting), Restricting));

    [Fact]
    public void NonPostgresErrors_AreNotRecognized()
    {
        Assert.False(PostgresErrors.IsDeleteBlockedByReference(new InvalidOperationException("x"), Restricting));
        Assert.False(PostgresErrors.IsForeignKeyViolation(new TimeoutException("x"), Restricting));
    }

    // Insert/update reference failures keep their exact semantics: 23503 only.
    [Fact]
    public void ForeignKeyViolation_StillMeansExactly23503()
    {
        Assert.True(PostgresErrors.IsForeignKeyViolation(Error(PostgresErrorCodes.ForeignKeyViolation, Restricting), Restricting));
        Assert.False(PostgresErrors.IsForeignKeyViolation(Error(PostgresErrorCodes.RestrictViolation, Restricting), Restricting));
        Assert.False(PostgresErrors.IsForeignKeyViolation(Error(PostgresErrorCodes.ForeignKeyViolation, Other), Restricting));
    }

    [Fact]
    public void UniqueViolation_StillMeansExactly23505()
    {
        Assert.True(PostgresErrors.IsUniqueViolation(Error(PostgresErrorCodes.UniqueViolation, "ux_x"), "ux_x"));
        Assert.False(PostgresErrors.IsUniqueViolation(Error(PostgresErrorCodes.ForeignKeyViolation, "ux_x"), "ux_x"));
    }

    private static PostgresException Error(string sqlState, string? constraintName) =>
        new("test", "ERROR", "ERROR", sqlState, constraintName: constraintName);
}
