using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AUTO-001 WP3A against real PostgreSQL: device_registrations schema and the repository's atomic
// upsert (row per owner, one Active row per installation, one row per token) under concurrency.
// Installation ids and tokens are unique per test: the token index is global.
[Collection(PostgreSqlCollection.Name)]
public class DeviceRegistrationPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _installation = Guid.NewGuid().ToString("D");
    private readonly string _token = "token-" + Guid.NewGuid().ToString("N");

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheDeviceRegistrationsTable()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddNotificationDeliveries", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "installation_id character varying(64) NOT NULL",
                "platform character varying(16) NOT NULL", "push_provider character varying(16) NOT NULL",
                "push_token character varying(4096) NULL", "status character varying(16) NOT NULL",
                "inactive_reason character varying(32) NULL", "created_at_utc timestamp with time zone NOT NULL",
                "updated_at_utc timestamp with time zone NOT NULL", "last_seen_at_utc timestamp with time zone NOT NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'device_registrations'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
                """));

        Assert.Equal(
            [
                "CREATE INDEX ix_device_registrations_user_active ON public.device_registrations USING btree (user_id) WHERE ((status)::text = 'Active'::text)",
                "CREATE UNIQUE INDEX ux_device_registrations_id_user ON public.device_registrations USING btree (id, user_id)",
                "CREATE UNIQUE INDEX ux_device_registrations_installation_active ON public.device_registrations USING btree (installation_id) WHERE ((status)::text = 'Active'::text)",
                "CREATE UNIQUE INDEX ux_device_registrations_installation_user ON public.device_registrations USING btree (installation_id, user_id)",
                "CREATE UNIQUE INDEX ux_device_registrations_token ON public.device_registrations USING btree (push_provider, push_token) WHERE (push_token IS NOT NULL)"
            ],
            await Strings(database,
                """
                SELECT indexdef AS "Value" FROM pg_indexes
                WHERE tablename = 'device_registrations' AND indexname <> 'PK_device_registrations' ORDER BY indexname
                """));
    }

    [Fact]
    public async Task InvalidStates_AreRejectedByTheDatabase()
    {
        var user = await NewUserAsync();
        await UpsertAsync(user.Id, _installation, _token);

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_device_registrations_state", () =>
            Execute($"UPDATE device_registrations SET push_token = NULL WHERE installation_id = '{_installation}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_device_registrations_state", () =>
            Execute($"UPDATE device_registrations SET status = 'Inactive' WHERE installation_id = '{_installation}'"));
        await PostgresAssert.ViolatesOneOfAsync(PostgresAssert.CheckViolation, ["ck_device_registrations_platform"], () =>
            Execute($"UPDATE device_registrations SET platform = 'Windows' WHERE installation_id = '{_installation}'"));
    }

    // ---- Upsert ----

    [Fact]
    public async Task FirstRegistration_ThenIdempotentReRegistration_KeepsTheRow()
    {
        var user = await NewUserAsync();

        Assert.True(await UpsertAsync(user.Id, _installation, _token, Now));
        var first = Assert.Single(await RowsAsync());
        Assert.True(await UpsertAsync(user.Id, _installation, _token, Now.AddHours(1)));

        var row = Assert.Single(await RowsAsync());
        Assert.Equal((first.Id, user.Id, DeviceRegistrationStatus.Active, _token, PushProvider.Fcm, DevicePlatform.Android),
            (row.Id, row.UserId, row.Status, row.PushToken, row.PushProvider, row.Platform));
        Assert.Equal((Now, Now.AddHours(1), Now.AddHours(1)), (row.CreatedAtUtc, row.UpdatedAtUtc, row.LastSeenAtUtc));
        Assert.Null(row.InactiveReason);
    }

    [Fact]
    public async Task TokenUpdate_AndPermissionDenied()
    {
        var user = await NewUserAsync();
        await UpsertAsync(user.Id, _installation, _token);

        await UpsertAsync(user.Id, _installation, _token + "-rotated");
        Assert.Equal(_token + "-rotated", Assert.Single(await RowsAsync()).PushToken);

        await UpsertAsync(user.Id, _installation, null);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.PermissionDenied), (row.Status, row.InactiveReason!.Value));
        Assert.Null(row.PushToken);
    }

    [Fact]
    public async Task OwnershipMovesBetweenUsers_WithARowPerOwner()
    {
        var a = await NewUserAsync();
        var b = await NewUserAsync();

        await UpsertAsync(a.Id, _installation, _token + "-a");
        var aRow = Assert.Single(await RowsAsync());

        // A signs out, B signs in on the same installation.
        await SignOutAsync(a.Id);
        await UpsertAsync(b.Id, _installation, _token + "-b");

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        var aAfter = rows.Single(row => row.UserId == a.Id);
        Assert.Equal((aRow.Id, DeviceRegistrationStatus.Inactive, DeviceInactiveReason.SignedOut), (aAfter.Id, aAfter.Status, aAfter.InactiveReason!.Value));
        Assert.Null(aAfter.PushToken);
        Assert.Equal((DeviceRegistrationStatus.Active, _token + "-b"), (rows.Single(row => row.UserId == b.Id).Status, rows.Single(row => row.UserId == b.Id).PushToken));

        // A signs in again without B signing out (offline sign-out): A's own row is reactivated.
        await UpsertAsync(a.Id, _installation, _token + "-a2");

        rows = await RowsAsync();
        Assert.Equal((aRow.Id, DeviceRegistrationStatus.Active), (rows.Single(row => row.UserId == a.Id).Id, rows.Single(row => row.UserId == a.Id).Status));
        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.SignedOut),
            (rows.Single(row => row.UserId == b.Id).Status, rows.Single(row => row.UserId == b.Id).InactiveReason!.Value));
    }

    [Fact]
    public async Task TheSameTokenOnAnotherInstallation_DeactivatesTheOlderRow()
    {
        var a = await NewUserAsync();
        var b = await NewUserAsync();
        var otherInstallation = Guid.NewGuid().ToString("D");

        await UpsertAsync(a.Id, _installation, _token);
        await UpsertAsync(b.Id, otherInstallation, _token);

        var old = Assert.Single(await RowsAsync(_installation));
        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.TokenInvalid), (old.Status, old.InactiveReason!.Value));
        Assert.Null(old.PushToken);
        Assert.Equal(DeviceRegistrationStatus.Active, Assert.Single(await RowsAsync(otherInstallation)).Status);
    }

    [Fact]
    public async Task ConcurrentRegistrationsOfOneInstallation_LeaveOneActiveOwner()
    {
        var users = new List<User>();

        for (var index = 0; index < 4; index++)
        {
            users.Add(await NewUserAsync());
        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            UpsertAsync(users[index % users.Count].Id, _installation, $"{_token}-{index}"))));

        var rows = await RowsAsync();
        Assert.Equal(users.Count, rows.Count);
        var active = Assert.Single(rows, row => row.Status == DeviceRegistrationStatus.Active);
        Assert.NotNull(active.PushToken);
        Assert.All(rows.Where(row => row.Status == DeviceRegistrationStatus.Inactive), row => Assert.Null(row.PushToken));
    }

    [Fact]
    public async Task ConcurrentRegistrationsOfOneToken_LeaveOneHolder()
    {
        var installations = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString("D")).ToList();
        var users = new List<User>();

        foreach (var _ in installations)
        {
            users.Add(await NewUserAsync());
        }

        await Task.WhenAll(installations.Select((installation, index) => Task.Run(() => UpsertAsync(users[index].Id, installation, _token))));

        await using var scope = fixture.CreateScope();
        var holders = await Db(scope).DeviceRegistrations.AsNoTracking().Where(row => row.PushToken == _token).ToListAsync();
        Assert.Single(holders);
        Assert.Equal(DeviceRegistrationStatus.Active, holders[0].Status);
    }

    [Fact]
    public async Task SignOut_IsOwnRowOnly()
    {
        var a = await NewUserAsync();
        var b = await NewUserAsync();
        await UpsertAsync(a.Id, _installation, _token);

        Assert.False(await SignOutAsync(b.Id));
        Assert.Equal(DeviceRegistrationStatus.Active, Assert.Single(await RowsAsync()).Status);
        Assert.True(await SignOutAsync(a.Id));
        Assert.True(await SignOutAsync(a.Id));
        Assert.Equal(DeviceInactiveReason.SignedOut, Assert.Single(await RowsAsync()).InactiveReason);
    }

    [Fact]
    public async Task PushTarget_OnlyForTheActiveOwnedRow_AndInvalidationKeepsANewerToken()
    {
        var a = await NewUserAsync();
        await UpsertAsync(a.Id, _installation, _token);
        var row = Assert.Single(await RowsAsync());

        await using (var scope = fixture.CreateScope())
        {
            var devices = Repository(scope);

            Assert.Equal(new PushTarget(PushProvider.Fcm, _token), await devices.GetPushTargetAsync(row.Id, a.Id, default));
            Assert.Null(await devices.GetPushTargetAsync(row.Id, Guid.CreateVersion7(), default));
            Assert.False(await devices.InvalidateTokenAsync(row.Id, "older-token", Now, default));
            Assert.True(await devices.InvalidateTokenAsync(row.Id, _token, Now, default));
            Assert.Null(await devices.GetPushTargetAsync(row.Id, a.Id, default));
        }

        Assert.Equal(DeviceInactiveReason.TokenInvalid, Assert.Single(await RowsAsync()).InactiveReason);
    }

    [Fact]
    public async Task MissingUser_IsNotRegistered_AndDeletingAUserDeletesItsRows()
    {
        Assert.False(await UpsertAsync(Guid.CreateVersion7(), _installation, _token));
        Assert.Empty(await RowsAsync());

        var user = await NewUserAsync();
        await UpsertAsync(user.Id, _installation, _token);
        await Execute($"DELETE FROM users WHERE id = '{user.Id}'");

        Assert.Empty(await RowsAsync());
    }

    // ---- Helpers ----

    private async Task<bool> UpsertAsync(Guid userId, string installationId, string? token, DateTimeOffset? nowUtc = null)
    {
        await using var scope = fixture.CreateScope();
        return await Repository(scope).UpsertAsync(
            DeviceRegistration.Register(userId, installationId, DevicePlatform.Android, token, token is not null, nowUtc ?? Now), default);
    }

    private async Task<bool> SignOutAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Repository(scope).SignOutAsync(userId, _installation, Now, default);
    }

    private async Task<List<DeviceRegistration>> RowsAsync(string? installationId = null)
    {
        var installation = installationId ?? _installation;
        await using var scope = fixture.CreateScope();
        return await Db(scope).DeviceRegistrations.AsNoTracking().Where(row => row.InstallationId == installation).ToListAsync();
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static IDeviceRegistrationRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IDeviceRegistrationRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
