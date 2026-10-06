using LifeOS.Application.Users;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AUTO-001 WP1 against real PostgreSQL: the AddAutomationFoundation migration adds only
// users.time_zone_id and its index, and the repository round-trips the zone. AddAutomationExecutions
// (WP2) adds only automation_executions; AddNotificationDeliveries (WP3A) only the two notification tables.
[Collection(PostgreSqlCollection.Name)]
public class UserTimeZonePersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_AddsOnlyTheNullableTimeZoneColumnAndItsIndex()
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

        var migration = database.GetMigrations().Single(id => id.EndsWith("_AddAutomationFoundation", StringComparison.Ordinal));
        Assert.Contains(migration, await database.GetAppliedMigrationsAsync());

        Assert.Equal(
            "time_zone_id character varying(64) NULL",
            await database.SqlQueryRaw<string>(
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'users'::regclass AND attname = 'time_zone_id' AND NOT attisdropped
                """).SingleAsync());
        Assert.Equal(
            "CREATE INDEX ix_users_time_zone_id ON public.users USING btree (time_zone_id)",
            await database.SqlQueryRaw<string>(
                "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_users_time_zone_id'").SingleAsync());

        // After WP3A: the AUTO-001 tables; weekly_reviews (AUTO-002) does not exist yet.
        Assert.Equal(["automation_executions", "device_registrations", "notification_deliveries"], (await database.SqlQueryRaw<string>(
            """
            SELECT table_name::text AS "Value" FROM information_schema.tables WHERE table_schema = 'public'
              AND table_name IN ('automation_executions', 'device_registrations', 'notification_deliveries', 'weekly_reviews')
            """).ToListAsync()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task UpdateTimeZone_RoundTrips_AndReportsMissingUsers()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        await using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            Assert.True(await users.UpdateTimeZoneAsync(user.Id, "Europe/Rome", CancellationToken.None));
            Assert.True(await users.UpdateTimeZoneAsync(user.Id, "America/New_York", CancellationToken.None));
            Assert.False(await users.UpdateTimeZoneAsync(Guid.CreateVersion7(), "Europe/Rome", CancellationToken.None));
        }

        await using var verify = fixture.CreateScope();
        var stored = await verify.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id, CancellationToken.None);
        Assert.Equal("America/New_York", stored!.TimeZoneId);
    }
}
