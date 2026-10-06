using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LifeOS.Infrastructure.Notifications;

// AUTO-001 §12 on PostgreSQL. The registration upsert is one transaction serialized per installation
// and per token (transaction-scoped advisory locks: a first registration has no row to lock), so the
// last writer holds the token. The unique indexes are the backstop: one row per (installation, user),
// one Active row per installation, one row per token; a violation (or deadlock) is retried.
internal sealed class DeviceRegistrationRepository(LifeOSDbContext db) : IDeviceRegistrationRepository
{
    private const int MaxUpsertAttempts = 3;

    public async Task<bool> UpsertAsync(DeviceRegistration registration, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await UpsertOnceAsync(registration, cancellationToken);
                return true;
            }
            catch (Exception exception) when (PostgresErrors.IsForeignKeyViolation(exception, DeviceRegistrationConfiguration.UserForeignKeyName))
            {
                return false;
            }
            catch (Exception exception) when (attempt < MaxUpsertAttempts && IsRetryable(exception))
            {
                // Another installation registered the same token concurrently: retry, now seeing it.
            }
        }
    }

    private async Task UpsertOnceAsync(DeviceRegistration registration, CancellationToken cancellationToken)
    {
        var now = registration.UpdatedAtUtc;
        var inactive = nameof(DeviceRegistrationStatus.Inactive);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Always installation first, then token: no lock-order cycle between registrations.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"device_registration:" + registration.InstallationId}, 0))",
            cancellationToken);

        if (registration.PushToken is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"device_token:" + registration.PushProvider + ":" + registration.PushToken}, 0))",
                cancellationToken);
        }

        // One signed-in owner per installation: the previous owner's row stays as its history.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE device_registrations
            SET status = {inactive}, inactive_reason = {nameof(DeviceInactiveReason.SignedOut)}, push_token = NULL, updated_at_utc = {now}
            WHERE installation_id = {registration.InstallationId} AND user_id <> {registration.UserId} AND status = 'Active'
            """, cancellationToken);

        // A token is never active for two rows (another user, or a stale installation).
        if (registration.PushToken is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE device_registrations
                SET status = {inactive}, inactive_reason = {nameof(DeviceInactiveReason.TokenInvalid)}, push_token = NULL, updated_at_utc = {now}
                WHERE push_provider = {registration.PushProvider.ToString()} AND push_token = {registration.PushToken}
                  AND NOT (installation_id = {registration.InstallationId} AND user_id = {registration.UserId})
                """, cancellationToken);
        }

        // The caller's own row: id and creation time are kept on update.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO device_registrations
                (id, user_id, installation_id, platform, push_provider, push_token, status, inactive_reason,
                 created_at_utc, updated_at_utc, last_seen_at_utc)
            VALUES
                ({registration.Id}, {registration.UserId}, {registration.InstallationId}, {registration.Platform.ToString()},
                 {registration.PushProvider.ToString()}, {registration.PushToken}, {registration.Status.ToString()},
                 {registration.InactiveReason?.ToString()}, {registration.CreatedAtUtc}, {now}, {registration.LastSeenAtUtc})
            ON CONFLICT (installation_id, user_id) DO UPDATE SET
                platform = EXCLUDED.platform,
                push_provider = EXCLUDED.push_provider,
                push_token = EXCLUDED.push_token,
                status = EXCLUDED.status,
                inactive_reason = EXCLUDED.inactive_reason,
                updated_at_utc = EXCLUDED.updated_at_utc,
                last_seen_at_utc = EXCLUDED.last_seen_at_utc
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> SignOutAsync(Guid userId, string installationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();

        var updated = await db.DeviceRegistrations
            .Where(registration => registration.UserId == userId && registration.InstallationId == installationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(registration => registration.Status, DeviceRegistrationStatus.Inactive)
                .SetProperty(registration => registration.InactiveReason, DeviceInactiveReason.SignedOut)
                .SetProperty(registration => registration.PushToken, (string?)null)
                .SetProperty(registration => registration.UpdatedAtUtc, now),
                cancellationToken);

        return updated > 0;
    }

    public async Task<PushTarget?> GetPushTargetAsync(Guid deviceRegistrationId, Guid userId, CancellationToken cancellationToken)
    {
        var target = await db.DeviceRegistrations
            .AsNoTracking()
            .Where(registration => registration.Id == deviceRegistrationId
                && registration.UserId == userId
                && registration.Status == DeviceRegistrationStatus.Active
                && registration.PushToken != null)
            .Select(registration => new { registration.PushProvider, registration.PushToken })
            .SingleOrDefaultAsync(cancellationToken);

        return target is null ? null : new PushTarget(target.PushProvider, target.PushToken!);
    }

    public async Task<bool> InvalidateTokenAsync(Guid deviceRegistrationId, string pushToken, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();

        var updated = await db.DeviceRegistrations
            .Where(registration => registration.Id == deviceRegistrationId && registration.PushToken == pushToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(registration => registration.Status, DeviceRegistrationStatus.Inactive)
                .SetProperty(registration => registration.InactiveReason, DeviceInactiveReason.TokenInvalid)
                .SetProperty(registration => registration.PushToken, (string?)null)
                .SetProperty(registration => registration.UpdatedAtUtc, now),
                cancellationToken);

        return updated == 1;
    }

    private static bool IsRetryable(Exception exception) =>
        PostgresErrors.IsUniqueViolation(
            exception,
            DeviceRegistrationConfiguration.TokenIndexName,
            DeviceRegistrationConfiguration.InstallationUserIndexName,
            DeviceRegistrationConfiguration.ActiveInstallationIndexName)
        || IsDeadlock(exception);

    private static bool IsDeadlock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState == PostgresErrorCodes.DeadlockDetected;
            }
        }

        return false;
    }
}
