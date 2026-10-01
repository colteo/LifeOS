using Npgsql;

namespace LifeOS.Infrastructure.Persistence;

// Recognizes an expected PostgreSQL integrity error by its SQLSTATE AND the exact name of the
// constraint, wherever it sits in the exception chain: SaveChanges wraps it in a DbUpdateException,
// ExecuteUpdate/ExecuteDelete do not. Any other error, including a violation of a different
// constraint, is not recognized and keeps propagating.
internal static class PostgresErrors
{
    // 23503: an INSERT/UPDATE references a row that does not exist (e.g. deleted concurrently).
    public static bool IsForeignKeyViolation(Exception exception, params IReadOnlyCollection<string> constraintNames) =>
        Is(exception, PostgresErrorCodes.ForeignKeyViolation, constraintNames);

    // A DELETE of a row that one of these ON DELETE RESTRICT foreign keys still references.
    // PostgreSQL 18 reports it as 23001 (restrict_violation), PostgreSQL 15-17 as 23503
    // (foreign_key_violation); both are accepted, but only for the named restricting keys, so a 23503
    // on any other constraint still propagates. Use only around deletes.
    public static bool IsDeleteBlockedByReference(Exception exception, params IReadOnlyCollection<string> constraintNames) =>
        Is(exception, PostgresErrorCodes.RestrictViolation, constraintNames)
        || Is(exception, PostgresErrorCodes.ForeignKeyViolation, constraintNames);

    // 23505: a unique index or key rejected the row.
    public static bool IsUniqueViolation(Exception exception, params IReadOnlyCollection<string> constraintNames) =>
        Is(exception, PostgresErrorCodes.UniqueViolation, constraintNames);

    private static bool Is(Exception exception, string sqlState, IReadOnlyCollection<string> constraintNames)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState == sqlState
                    && postgres.ConstraintName is { } name
                    && constraintNames.Contains(name);
            }
        }

        return false;
    }
}
