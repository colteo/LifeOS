using LifeOS.Application.Notifications.Devices;
using LifeOS.Domain.Notifications;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Notifications;

// AUTO-001 §12 rules: installation id and token validation, Active/Inactive states, row-per-owner
// re-ownership and token uniqueness (in-memory; PostgreSQL atomicity in LifeOS.IntegrationTests).
public class DeviceRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserA = Guid.Parse("0192f0c3-0000-7000-8000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("0192f0c3-0000-7000-8000-00000000000b");
    private const string Installation = "5f0c1d2e-3a4b-4c5d-8e9f-0a1b2c3d4e5f";

    private readonly InMemoryDeviceRegistrationRepository _devices = new();

    // ---- Domain ----

    [Fact]
    public void Register_Permitted_IsActiveWithTheToken()
    {
        var registration = DeviceRegistration.Register(UserA, Installation, DevicePlatform.Android, "token", true, Now);

        Assert.Equal(7, registration.Id.Version);
        Assert.Equal((DeviceRegistrationStatus.Active, PushProvider.Fcm, "token"), (registration.Status, registration.PushProvider, registration.PushToken));
        Assert.Null(registration.InactiveReason);
        Assert.Equal((Now, Now, Now), (registration.CreatedAtUtc, registration.UpdatedAtUtc, registration.LastSeenAtUtc));
    }

    [Fact]
    public void Register_NotPermitted_IsInactiveWithoutAToken()
    {
        var registration = DeviceRegistration.Register(UserA, Installation, DevicePlatform.Android, null, false, Now);

        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.PermissionDenied), (registration.Status, registration.InactiveReason!.Value));
        Assert.Null(registration.PushToken);
    }

    [Theory]
    [InlineData(Installation, true)]
    [InlineData("abcdefghijklmnop", true)]
    [InlineData("abcdefghijklmno", false)]       // 15: too short to be unguessable
    [InlineData("installation with spaces 0001", false)]
    [InlineData("../../etc/passwd-0000000", false)]
    [InlineData("", false)]
    public void InstallationIds_AreBounded(string installationId, bool valid)
    {
        Assert.Equal(valid, DeviceRegistration.IsValidInstallationId(installationId));
        Assert.False(DeviceRegistration.IsValidInstallationId(new string('a', 65)));
    }

    [Fact]
    public void PushTokens_AreBoundedVisibleAscii()
    {
        Assert.True(DeviceRegistration.IsValidPushToken("dGVzdA:APA91b-_x"));
        Assert.True(DeviceRegistration.IsValidPushToken(new string('a', 4096)));
        Assert.False(DeviceRegistration.IsValidPushToken(new string('a', 4097)));
        Assert.False(DeviceRegistration.IsValidPushToken(""));
        Assert.False(DeviceRegistration.IsValidPushToken("with space"));
        Assert.False(DeviceRegistration.IsValidPushToken(null));
    }

    // ---- Handlers ----

    [Theory]
    [InlineData("short", "Android", "token", true, "installationId")]
    [InlineData(Installation, "iOS", "token", true, "platform")]
    [InlineData(Installation, null, "token", true, "platform")]
    [InlineData(Installation, "Android", "token", null, "notificationsPermitted")]
    [InlineData(Installation, "Android", null, true, "pushToken")]
    [InlineData(Installation, "Android", "", true, "pushToken")]
    [InlineData(Installation, "Android", "token", false, "pushToken")]
    public async Task Register_InvalidInput_IsRejected_WithoutWriting(string installationId, string? platform, string? token, bool? permitted, string field)
    {
        var result = await new RegisterDeviceHandler(_devices, new FixedTimeProvider(Now))
            .HandleAsync(UserA, new RegisterDeviceCommand(installationId, platform, token, permitted), default);

        Assert.Equal(DeviceRegistrationOutcome.Invalid, result.Outcome);
        Assert.Contains(field, result.Errors.Keys);
        Assert.Empty(_devices.Rows);
    }

    [Fact]
    public async Task Register_ForAMissingUser_IsNotFound()
    {
        _devices.ExistingUsers = [];

        var result = await new RegisterDeviceHandler(_devices, new FixedTimeProvider(Now))
            .HandleAsync(UserA, new RegisterDeviceCommand(Installation, "Android", "token", true), default);

        Assert.Equal(DeviceRegistrationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Unregister_OnlyTouchesTheCallersOwnRow()
    {
        await RegisterAsync(UserA, Installation, "token-a");
        var handler = new UnregisterDeviceHandler(_devices, new FixedTimeProvider(Now));

        Assert.Equal(DeviceRegistrationOutcome.NotFound, (await handler.HandleAsync(UserB, Installation, default)).Outcome);
        Assert.Equal(DeviceRegistrationStatus.Active, _devices.Single(UserA, Installation).Status);

        Assert.Equal(DeviceRegistrationOutcome.Done, (await handler.HandleAsync(UserA, Installation, default)).Outcome);
        Assert.Equal(DeviceRegistrationOutcome.Done, (await handler.HandleAsync(UserA, Installation, default)).Outcome);
        Assert.Equal(DeviceRegistrationOutcome.Invalid, (await handler.HandleAsync(UserA, "short", default)).Outcome);

        var row = _devices.Single(UserA, Installation);
        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.SignedOut), (row.Status, row.InactiveReason!.Value));
        Assert.Null(row.PushToken);
    }

    // ---- Re-ownership and token uniqueness (same rules as the PostgreSQL repository) ----

    [Fact]
    public async Task AnotherUserOnTheSameInstallation_GetsItsOwnRow_AndThePreviousOwnerIsSignedOut()
    {
        await RegisterAsync(UserA, Installation, "token-a");
        var aRowId = _devices.Single(UserA, Installation).Id;

        await RegisterAsync(UserB, Installation, "token-b");

        Assert.Equal(2, _devices.Rows.Count);
        var a = _devices.Single(UserA, Installation);
        Assert.Equal((aRowId, DeviceRegistrationStatus.Inactive, DeviceInactiveReason.SignedOut), (a.Id, a.Status, a.InactiveReason!.Value));
        Assert.Equal(("token-b", DeviceRegistrationStatus.Active), (_devices.Single(UserB, Installation).PushToken, _devices.Single(UserB, Installation).Status));

        // A signs in again: A's original row is reactivated, B's is signed out.
        await RegisterAsync(UserA, Installation, "token-a2");

        Assert.Equal((aRowId, DeviceRegistrationStatus.Active), (_devices.Single(UserA, Installation).Id, _devices.Single(UserA, Installation).Status));
        Assert.Equal(DeviceRegistrationStatus.Inactive, _devices.Single(UserB, Installation).Status);
        Assert.Single(_devices.Rows, row => row.Status == DeviceRegistrationStatus.Active);
    }

    [Fact]
    public async Task SameTokenOnAnotherInstallation_DeactivatesTheOlderRow()
    {
        await RegisterAsync(UserA, Installation, "shared-token");
        await RegisterAsync(UserB, "another-installation-0001", "shared-token");

        var old = _devices.Single(UserA, Installation);
        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.TokenInvalid), (old.Status, old.InactiveReason!.Value));
        Assert.Null(old.PushToken);
        Assert.Single(_devices.Rows, row => row.PushToken == "shared-token");
    }

    private Task<bool> RegisterAsync(Guid userId, string installationId, string token) =>
        _devices.UpsertAsync(DeviceRegistration.Register(userId, installationId, DevicePlatform.Android, token, true, Now), default);
}
