using LifeOS.Application.Automation;
using LifeOS.Application.Notifications;
using LifeOS.Application.Persistence;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AUTO-001 WP3A against real PostgreSQL: notification_deliveries schema, idempotent fan-out, claims
// with FOR UPDATE SKIP LOCKED, lease takeover, fenced completion, terminal sweeps, the dispatcher
// with a test-only sender, and the unit of work that makes a fenced automation completion and its
// deliveries atomic.
//
// Delivery claims and sweeps are not scoped by type, so each test works in its own time window
// (10 days apart, every delivery expires within a day): no test ever claims another test's rows.
[Collection(PostgreSqlCollection.Name)]
public class NotificationDeliveryPersistenceTests(PostgreSqlFixture fixture)
{
    private static int _windows;

    private readonly DateTimeOffset _now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero)
        .AddDays(10 * Interlocked.Increment(ref _windows));

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheNotificationDeliveriesTable()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "device_registration_id uuid NOT NULL",
                "notification_key character varying(128) NOT NULL", "notification_type character varying(32) NOT NULL",
                "source_execution_id uuid NULL", "resource_type character varying(32) NULL", "resource_id uuid NULL",
                "status character varying(16) NOT NULL", "attempt_count integer NOT NULL",
                "next_attempt_at_utc timestamp with time zone NULL", "lease_expires_at_utc timestamp with time zone NULL",
                "expires_at_utc timestamp with time zone NOT NULL", "last_error_code character varying(32) NULL",
                "created_at_utc timestamp with time zone NOT NULL", "sent_at_utc timestamp with time zone NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'notification_deliveries'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
                """));

        var indexes = await Strings(database,
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'notification_deliveries' ORDER BY indexname");
        Assert.Contains("CREATE UNIQUE INDEX ux_notification_deliveries_device ON public.notification_deliveries USING btree (notification_key, device_registration_id)", indexes);
        Assert.Contains("CREATE INDEX ix_notification_deliveries_due ON public.notification_deliveries USING btree (next_attempt_at_utc) WHERE ((status)::text = 'Pending'::text)", indexes);
        Assert.Contains("CREATE INDEX ix_notification_deliveries_stale ON public.notification_deliveries USING btree (lease_expires_at_utc) WHERE ((status)::text = 'Sending'::text)", indexes);

        // FKs: c = cascade, n = set null; the device FK is composite (device, user).
        Assert.Equal(
            [
                "FK_notification_deliveries_device_registrations c (device_registration_id, user_id)",
                "FK_notification_deliveries_source_execution n (source_execution_id)",
                "FK_notification_deliveries_users_user_id c (user_id)"
            ],
            (await Strings(database,
                """
                SELECT conname::text || ' ' || confdeltype::text || ' (' ||
                    (SELECT string_agg(attname::text, ', ' ORDER BY array_position(conkey, attnum)) FROM pg_attribute
                     WHERE attrelid = conrelid AND attnum = ANY(conkey)) || ')' AS "Value"
                FROM pg_constraint WHERE conrelid = 'notification_deliveries'::regclass AND contype = 'f'
                """)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ADeliveryForAnotherUsersDevice_IsRejectedByTheCompositeKey()
    {
        var (owner, device) = await DeviceAsync();
        var other = await NewUserAsync();

        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_notification_deliveries_device_registrations", () =>
            Execute($"""
                INSERT INTO notification_deliveries (id, user_id, device_registration_id, notification_key, notification_type, status,
                    attempt_count, next_attempt_at_utc, expires_at_utc, created_at_utc)
                VALUES ('{Guid.CreateVersion7()}', '{other.Id}', '{device.Id}', 'test:x', 'Test', 'Pending', 0, now(), now() + interval '1 hour', now())
                """));
        Assert.NotEqual(owner.Id, other.Id);
    }

    // ---- Fan-out ----

    [Fact]
    public async Task Enqueue_OneRowPerActiveDevice_Idempotent_EvenConcurrently()
    {
        var (user, phone) = await DeviceAsync();
        var tablet = await DeviceAsync(user);
        await DeviceAsync(user, permitted: false);
        var notification = Notification(user.Id);

        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => EnqueueAsync(notification))));

        Assert.Equal(2, created.Sum());
        var rows = await RowsAsync(user.Id);
        Assert.Equal(new[] { phone.Id, tablet.Id }.Order(), rows.Select(row => row.DeviceRegistrationId).Order());
        Assert.All(rows, row =>
        {
            Assert.Equal((NotificationDeliveryStatus.Pending, 0, _now, notification.ExpiresAtUtc), (row.Status, row.AttemptCount, row.NextAttemptAtUtc!.Value, row.ExpiresAtUtc));
            Assert.Equal((notification.NotificationKey, NotificationType.Test), (row.NotificationKey, row.NotificationType));
        });

        Assert.Equal(0, await EnqueueAsync(notification));
    }

    [Fact]
    public async Task UnitOfWork_RollsBackEnqueuedDeliveries_WhenTheWorkDeclinesOrThrows()
    {
        var (user, _) = await DeviceAsync();

        await using (var scope = fixture.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var deliveries = Deliveries(scope);

            Assert.False(await unitOfWork.TryInTransactionAsync(async ct =>
            {
                await deliveries.EnqueueAsync(Notification(user.Id), _now, ct);
                return false;
            }, default));

            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.TryInTransactionAsync(async ct =>
            {
                await deliveries.EnqueueAsync(Notification(user.Id), _now, ct);
                throw new InvalidOperationException();
            }, default));

            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.TryInTransactionAsync(
                ct => unitOfWork.TryInTransactionAsync(_ => Task.FromResult(true), ct), default));
        }

        Assert.Empty(await RowsAsync(user.Id));
    }

    // ---- Claims ----

    [Fact]
    public async Task ConcurrentClaims_EachDeliveryHasOneClaimer()
    {
        var (user, _) = await DeviceAsync();

        for (var index = 0; index < 5; index++)
        {
            await EnqueueAsync(Notification(user.Id));
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ClaimAsync(_now))));
        var claimed = claims.OfType<NotificationDeliveryWorkItem>().ToList();

        Assert.Equal((await RowsAsync(user.Id)).Select(row => row.Id).Order(), claimed.Select(item => item.DeliveryId).Order());
        Assert.All(await RowsAsync(user.Id), row => Assert.Equal((NotificationDeliveryStatus.Sending, 1, _now + Lease), (row.Status, row.AttemptCount, row.LeaseExpiresAtUtc!.Value)));
    }

    [Fact]
    public async Task Claims_TakeOnlyEligibleRows_OldestFirst()
    {
        var (user, _) = await DeviceAsync();
        var first = await EnqueueOneAsync(user.Id, _now);
        var later = await EnqueueOneAsync(user.Id, _now.AddMinutes(1));
        var stale = await EnqueueOneAsync(user.Id, _now.AddMinutes(2));
        var exhausted = await EnqueueOneAsync(user.Id, _now.AddMinutes(3));

        // stale and exhausted are claimed and crash; exhausted on its last attempt.
        var staleClaim = await ClaimSpecificAsync(stale, _now.AddMinutes(2));
        var exhaustedClaim = await ClaimSpecificAsync(exhausted, _now.AddMinutes(3));
        await Execute($"UPDATE notification_deliveries SET attempt_count = {NotificationDeliveryPolicy.MaxAttempts} WHERE id = '{exhausted}'");

        // Not yet due.
        Assert.Null(await ClaimAsync(_now.AddSeconds(-1)));

        var at = _now.AddMinutes(10);
        var order = new List<Guid>();

        while (await ClaimAsync(at) is { } item)
        {
            order.Add(item.DeliveryId);
        }

        // Due Pending rows by due time, then the stale lease (expired at +7 min); never the exhausted one.
        Assert.Equal([first, later, stale], order);
        Assert.Equal(2, (await RowAsync(stale)).AttemptCount);
        Assert.Equal(NotificationDeliveryStatus.Sending, (await RowAsync(exhausted)).Status);
        Assert.NotNull(staleClaim);
        Assert.NotNull(exhaustedClaim);
    }

    [Fact]
    public async Task Completion_IsFencedByTheAttempt_AfterATakeover()
    {
        var (user, _) = await DeviceAsync();
        var id = await EnqueueOneAsync(user.Id, _now);
        var crashed = (await ClaimAsync(_now))!;
        var takeover = (await ClaimAsync(_now + Lease))!;
        Assert.Equal((id, 2), (takeover.DeliveryId, takeover.Attempt));

        await using (var scope = fixture.CreateScope())
        {
            var deliveries = Deliveries(scope);

            Assert.False(await deliveries.CompleteSentAsync(id, crashed.Attempt, _now, default));
            Assert.False(await deliveries.CompleteFailedAsync(id, crashed.Attempt, NotificationErrorCode.Rejected, _now, default));
            Assert.False(await deliveries.CompleteRetryAsync(id, crashed.Attempt, NotificationErrorCode.Transient, _now.AddHours(1), default));
            Assert.True(await deliveries.CompleteSentAsync(id, takeover.Attempt, _now.AddMinutes(6), default));
            Assert.False(await deliveries.CompleteSentAsync(id, takeover.Attempt, _now.AddMinutes(7), default));
        }

        var row = await RowAsync(id);
        Assert.Equal((NotificationDeliveryStatus.Sent, 2, _now.AddMinutes(6)), (row.Status, row.AttemptCount, row.SentAtUtc!.Value));
        Assert.Null(row.LeaseExpiresAtUtc);
        Assert.Null(row.LastErrorCode);
    }

    [Fact]
    public async Task FinalizeAbandoned_ExpiresAndExhaustsOnlyAbandonedRows()
    {
        var (user, _) = await DeviceAsync();
        var pendingExpired = await EnqueueOneAsync(user.Id, _now, TimeSpan.FromMinutes(15));
        var staleExhausted = await EnqueueOneAsync(user.Id, _now);
        await ClaimSpecificAsync(staleExhausted, _now.AddMinutes(1));
        await Execute($"UPDATE notification_deliveries SET attempt_count = {NotificationDeliveryPolicy.MaxAttempts} WHERE id = '{staleExhausted}'");
        var pendingFresh = await EnqueueOneAsync(user.Id, _now.AddMinutes(20));
        var at = _now.AddMinutes(20);

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Deliveries(scope).FinalizeAbandonedAsync(at, NotificationDeliveryPolicy.MaxAttempts, 1000, default) >= 2);
        }

        Assert.Equal((NotificationDeliveryStatus.Failed, NotificationErrorCode.Expired), Final(await RowAsync(pendingExpired)));
        Assert.Equal((NotificationDeliveryStatus.Failed, NotificationErrorCode.MaxAttempts), Final(await RowAsync(staleExhausted)));
        Assert.Equal(NotificationDeliveryStatus.Pending, (await RowAsync(pendingFresh)).Status);

        static (NotificationDeliveryStatus, NotificationErrorCode?) Final(NotificationDelivery row) => (row.Status, row.LastErrorCode);
    }

    // ---- Dispatcher on PostgreSQL ----

    [Fact]
    public async Task Dispatch_RecordsSentRetryAndTokenInvalid()
    {
        var (user, phone) = await DeviceAsync();
        var tablet = await DeviceAsync(user);
        var watch = await DeviceAsync(user);
        await EnqueueAsync(Notification(user.Id, TimeSpan.FromDays(1)));
        var sender = new FakePushNotificationSender
        {
            Respond = (target, _) => Task.FromResult(
                target.Token == phone.PushToken ? PushSendResult.Accepted
                : target.Token == tablet.PushToken ? PushSendResult.Transient
                : PushSendResult.TokenInvalid)
        };

        for (var index = 0; index < 3; index++)
        {
            await using var scope = fixture.CreateScope();
            Assert.True(await Dispatcher(scope, sender).DispatchNextAsync());
        }

        var rows = await RowsAsync(user.Id);
        var sent = rows.Single(row => row.DeviceRegistrationId == phone.Id);
        Assert.Equal((NotificationDeliveryStatus.Sent, _now), (sent.Status, sent.SentAtUtc!.Value));
        var retry = rows.Single(row => row.DeviceRegistrationId == tablet.Id);
        Assert.Equal((NotificationDeliveryStatus.Pending, NotificationErrorCode.Transient, _now.AddMinutes(10)), (retry.Status, retry.LastErrorCode!.Value, retry.NextAttemptAtUtc!.Value));
        var invalid = rows.Single(row => row.DeviceRegistrationId == watch.Id);
        Assert.Equal((NotificationDeliveryStatus.Failed, NotificationErrorCode.TokenInvalid), (invalid.Status, invalid.LastErrorCode!.Value));

        await using (var verify = fixture.CreateScope())
        {
            var watchRow = await Db(verify).DeviceRegistrations.AsNoTracking().SingleAsync(row => row.Id == watch.Id);
            Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.TokenInvalid), (watchRow.Status, watchRow.InactiveReason!.Value));
            Assert.Null(watchRow.PushToken);

            // The retry is not due yet.
            Assert.False(await Dispatcher(verify, sender).DispatchNextAsync());
        }

        Assert.Equal(3, sender.Sent.Count);
    }

    // A's pending delivery on an installation re-owned by B fails without reaching B's token.
    [Fact]
    public async Task Dispatch_ToAReownedInstallation_DoesNotReachTheNewOwner()
    {
        var (a, aDevice) = await DeviceAsync();
        await EnqueueAsync(Notification(a.Id));
        var b = await NewUserAsync();
        await RegisterAsync(b.Id, aDevice.InstallationId, "token-" + Guid.NewGuid().ToString("N"));
        var sender = new FakePushNotificationSender();

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Dispatcher(scope, sender).DispatchNextAsync());
        }

        Assert.Empty(sender.Sent);
        var row = Assert.Single(await RowsAsync(a.Id));
        Assert.Equal((aDevice.Id, NotificationDeliveryStatus.Failed, NotificationErrorCode.DeviceInactive), (row.DeviceRegistrationId, row.Status, row.LastErrorCode!.Value));
    }

    // ---- The tick on PostgreSQL ----

    [Fact]
    public async Task SucceededExecution_EnqueuesDeliveries_AndTheNextTickSendsThem()
    {
        var (user, _) = await DeviceAsync();
        await DeviceAsync(user);
        var handler = new TestAutomationHandler($"Test-{Guid.NewGuid():N}");
        handler.AddDue(user.Id, "2030-01-01", _now.AddMinutes(-1));
        handler.Execute = _ => Task.FromResult(AutomationResult.Succeeded(null, new AutomationNotification(NotificationType.Test)));
        var sender = new FakePushNotificationSender();
        var clock = new ManualTimeProvider(_now);

        var first = await TickAsync(handler, sender, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await TickAsync(handler, sender, clock);

        Assert.Equal((0, 1), (first.Deliveries, first.Executions));
        Assert.Equal((2, 0), (second.Deliveries, second.Executions));

        await using var scope = fixture.CreateScope();
        var execution = await Db(scope).AutomationExecutions.AsNoTracking().SingleAsync(row => row.AutomationType == handler.AutomationType);
        var rows = await RowsAsync(user.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(("automation:" + execution.Id.ToString("D"), execution.Id), (row.NotificationKey, row.SourceExecutionId!.Value));
            Assert.Equal(NotificationDeliveryStatus.Sent, row.Status);
        });
        Assert.Equal(2, sender.Sent.Count);
    }

    // §8: when the fenced completion loses (another attempt took the lease over), the deliveries of the
    // stale attempt are rolled back with it.
    [Fact]
    public async Task FencedOutCompletion_RollsBackItsDeliveries()
    {
        var (user, _) = await DeviceAsync();
        var handler = new TestAutomationHandler($"Test-{Guid.NewGuid():N}");
        handler.AddDue(user.Id, "2030-01-01", _now.AddMinutes(-1));
        var clock = new ManualTimeProvider(_now);

        handler.Execute = async occurrence =>
        {
            if (occurrence.Attempt == 1)
            {
                clock.Advance(AutomationExecutionPolicy.Lease);
                await TickAsync(handler, sender: null, clock);
                return AutomationResult.Succeeded(null, new AutomationNotification(NotificationType.Test));
            }

            return AutomationResult.Succeeded();
        };

        await TickAsync(handler, sender: null, clock);

        Assert.Empty(await RowsAsync(user.Id));
        await using var scope = fixture.CreateScope();
        var execution = await Db(scope).AutomationExecutions.AsNoTracking().SingleAsync(row => row.AutomationType == handler.AutomationType);
        Assert.Equal((AutomationExecutionStatus.Succeeded, 2), (execution.Status, execution.AttemptCount));
    }

    [Fact]
    public async Task DeletingTheExecution_KeepsTheDeliveryHistory_AndDeletingTheUserRemovesIt()
    {
        var (user, _) = await DeviceAsync();
        var execution = AutomationExecution.Claim(user.Id, $"Test-{Guid.NewGuid():N}", "k", "Europe/Rome", _now.AddMinutes(-1), _now.AddHours(1), _now, AutomationExecutionPolicy.Lease);
        await PostgresAssert.InsertAsync(fixture, execution);
        await EnqueueAsync(LogicalNotification.ForAutomation(execution.Id, user.Id, NotificationType.Test, null, null, _now));

        await Execute($"DELETE FROM automation_executions WHERE id = '{execution.Id}'");
        Assert.Null(Assert.Single(await RowsAsync(user.Id)).SourceExecutionId);

        await Execute($"DELETE FROM users WHERE id = '{user.Id}'");
        Assert.Empty(await RowsAsync(user.Id));
    }

    // ---- Test notification (WP3B) ----

    [Fact]
    public async Task ClaimForNotification_TakesOnlyThatNotificationsRows()
    {
        var (user, _) = await DeviceAsync();
        await DeviceAsync(user);
        var older = Notification(user.Id);
        await EnqueueAsync(older);
        var mine = Notification(user.Id);
        await EnqueueAsync(mine);

        var claimed = new List<NotificationDeliveryWorkItem>();

        await using (var scope = fixture.CreateScope())
        {
            while (await Deliveries(scope).ClaimNextForNotificationAsync(mine.NotificationKey, _now, Lease, NotificationDeliveryPolicy.MaxAttempts, default) is { } item)
            {
                claimed.Add(item);
            }
        }

        Assert.Equal(2, claimed.Count);
        Assert.All(claimed, item => Assert.Equal(mine.NotificationKey, item.NotificationKey));
        Assert.All((await RowsAsync(user.Id)).Where(row => row.NotificationKey == older.NotificationKey), row => Assert.Equal(NotificationDeliveryStatus.Pending, row.Status));
    }

    [Fact]
    public async Task TestNotification_SendsInlineThroughTheDeliveryRows()
    {
        var (user, phone) = await DeviceAsync();
        var tablet = await DeviceAsync(user);
        var (other, _) = await DeviceAsync();
        var sender = new FakePushNotificationSender
        {
            Respond = (target, _) => Task.FromResult(target.Token == phone.PushToken ? PushSendResult.Accepted : PushSendResult.TokenInvalid)
        };

        TestNotificationResult result;

        await using (var scope = fixture.CreateScope())
        {
            result = await new SendTestNotificationHandler(Deliveries(scope), Dispatcher(scope, sender), new FixedTimeProvider(_now)).HandleAsync(user.Id, default);
        }

        Assert.Equal(new TestNotificationResult(TestNotificationOutcome.Sent, 2, 1, 1), result);
        var rows = await RowsAsync(user.Id);
        Assert.Equal(
            [(phone.Id, NotificationDeliveryStatus.Sent), (tablet.Id, NotificationDeliveryStatus.Failed)],
            rows.OrderBy(row => row.Status).Select(row => (row.DeviceRegistrationId, row.Status)));
        Assert.All(rows, row => Assert.Equal((NotificationType.Test, _now.AddMinutes(15)), (row.NotificationType, row.ExpiresAtUtc)));
        Assert.Empty(await RowsAsync(other.Id));

        await using var verify = fixture.CreateScope();
        Assert.Equal(DeviceRegistrationStatus.Inactive, (await Db(verify).DeviceRegistrations.AsNoTracking().SingleAsync(row => row.Id == tablet.Id)).Status);
    }

    // ---- Helpers ----

    private static readonly TimeSpan Lease = NotificationDeliveryPolicy.Lease;

    private LogicalNotification Notification(Guid userId, TimeSpan? expiresIn = null) =>
        LogicalNotification.Test(userId, _now) with { ExpiresAtUtc = _now + (expiresIn ?? TimeSpan.FromMinutes(15)) };

    private async Task<int> EnqueueAsync(LogicalNotification notification)
    {
        await using var scope = fixture.CreateScope();
        return await Deliveries(scope).EnqueueAsync(notification, _now, default);
    }

    // One delivery created at `createdAtUtc` (due then); returns its id.
    private async Task<Guid> EnqueueOneAsync(Guid userId, DateTimeOffset createdAtUtc, TimeSpan? expiresIn = null)
    {
        var notification = LogicalNotification.Test(userId, createdAtUtc) with { ExpiresAtUtc = createdAtUtc + (expiresIn ?? TimeSpan.FromHours(23)) };

        await using var scope = fixture.CreateScope();
        Assert.Equal(1, await Deliveries(scope).EnqueueAsync(notification, createdAtUtc, default));

        return (await Db(scope).NotificationDeliveries.AsNoTracking().SingleAsync(row => row.NotificationKey == notification.NotificationKey)).Id;
    }

    private async Task<NotificationDeliveryWorkItem?> ClaimAsync(DateTimeOffset nowUtc)
    {
        await using var scope = fixture.CreateScope();
        return await Deliveries(scope).ClaimNextAsync(nowUtc, Lease, NotificationDeliveryPolicy.MaxAttempts, default);
    }

    // Claims exactly this row (the oldest due one at its own due time in this test's window).
    private async Task<NotificationDeliveryWorkItem> ClaimSpecificAsync(Guid id, DateTimeOffset nowUtc)
    {
        await Execute($"UPDATE notification_deliveries SET status = 'Sending', attempt_count = 1, next_attempt_at_utc = NULL, lease_expires_at_utc = '{(nowUtc + Lease).UtcDateTime:O}' WHERE id = '{id}'");
        var row = await RowAsync(id);

        return new NotificationDeliveryWorkItem(row.Id, row.AttemptCount, row.UserId, row.DeviceRegistrationId, row.NotificationKey, row.NotificationType, row.ResourceType, row.ResourceId, row.ExpiresAtUtc);
    }

    private async Task<AutomationTickResult> TickAsync(IAutomationHandler handler, IPushNotificationSender? sender, TimeProvider time)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;

        return await new RunAutomationTick(
            [handler],
            services.GetRequiredService<IAutomationExecutionStore>(),
            Dispatcher(scope, sender, time),
            Deliveries(scope),
            services.GetRequiredService<IUnitOfWork>(),
            new AutomationTickGuard(),
            time).RunAsync();
    }

    private NotificationDispatcher Dispatcher(AsyncServiceScope scope, IPushNotificationSender? sender, TimeProvider? time = null) =>
        new(Deliveries(scope), scope.ServiceProvider.GetRequiredService<IDeviceRegistrationRepository>(),
            scope.ServiceProvider.GetRequiredService<INotificationPreferencesRepository>(), scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), time ?? new FixedTimeProvider(_now), sender);

    private async Task<List<NotificationDelivery>> RowsAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).NotificationDeliveries.AsNoTracking().Where(row => row.UserId == userId).ToListAsync();
    }

    private async Task<NotificationDelivery> RowAsync(Guid id)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).NotificationDeliveries.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private async Task<(User User, DeviceRegistration Device)> DeviceAsync()
    {
        var user = await NewUserAsync();
        return (user, await DeviceAsync(user));
    }

    private async Task<DeviceRegistration> DeviceAsync(User user, bool permitted = true)
    {
        var installation = Guid.NewGuid().ToString("D");
        await RegisterAsync(user.Id, installation, permitted ? "token-" + Guid.NewGuid().ToString("N") : null);

        await using var scope = fixture.CreateScope();
        return await Db(scope).DeviceRegistrations.AsNoTracking().SingleAsync(row => row.InstallationId == installation && row.UserId == user.Id);
    }

    private async Task RegisterAsync(Guid userId, string installationId, string? token)
    {
        await using var scope = fixture.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDeviceRegistrationRepository>().UpsertAsync(
            DeviceRegistration.Register(userId, installationId, DevicePlatform.Android, token, token is not null, _now), default));
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, _now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static INotificationDeliveryStore Deliveries(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<INotificationDeliveryStore>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
