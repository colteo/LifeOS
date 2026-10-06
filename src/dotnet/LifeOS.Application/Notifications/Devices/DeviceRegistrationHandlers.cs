using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Notifications.Devices;

public sealed record RegisterDeviceCommand(string? InstallationId, string? Platform, string? PushToken, bool? NotificationsPermitted);

public enum DeviceRegistrationOutcome
{
    Done,
    Invalid,
    NotFound
}

public sealed record DeviceRegistrationResult(DeviceRegistrationOutcome Outcome, IReadOnlyDictionary<string, string[]> Errors)
{
    public static readonly DeviceRegistrationResult Done = new(DeviceRegistrationOutcome.Done, new Dictionary<string, string[]>());

    public static readonly DeviceRegistrationResult NotFound = new(DeviceRegistrationOutcome.NotFound, new Dictionary<string, string[]>());

    public static DeviceRegistrationResult Invalid(IReadOnlyDictionary<string, string[]> errors) => new(DeviceRegistrationOutcome.Invalid, errors);
}

// AUTO-001 §12: PUT /api/devices/{installationId}. The owner is always the signed-in user (never a
// payload field). Idempotent: the same registration twice leaves the same row.
public sealed class RegisterDeviceHandler(IDeviceRegistrationRepository devices, TimeProvider time)
{
    public async Task<DeviceRegistrationResult> HandleAsync(Guid userId, RegisterDeviceCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (!DeviceRegistration.IsValidInstallationId(command.InstallationId))
        {
            errors["installationId"] = [$"Must be {DeviceRegistration.MinInstallationIdLength}–{DeviceRegistration.MaxInstallationIdLength} characters of letters, digits, '-' or '_'."];
        }

        if (command.Platform != nameof(DevicePlatform.Android))
        {
            errors["platform"] = ["Must be 'Android'."];
        }

        if (command.NotificationsPermitted is null)
        {
            errors["notificationsPermitted"] = ["Required."];
        }
        else if (command.NotificationsPermitted.Value && !DeviceRegistration.IsValidPushToken(command.PushToken))
        {
            errors["pushToken"] = [$"Required when notifications are permitted: 1–{DeviceRegistration.MaxPushTokenLength} visible ASCII characters."];
        }
        else if (!command.NotificationsPermitted.Value && command.PushToken is not null)
        {
            errors["pushToken"] = ["Must be omitted when notifications are not permitted."];
        }

        if (errors.Count > 0)
        {
            return DeviceRegistrationResult.Invalid(errors);
        }

        var registration = DeviceRegistration.Register(
            userId,
            command.InstallationId!,
            DevicePlatform.Android,
            command.PushToken,
            command.NotificationsPermitted!.Value,
            time.GetUtcNow());

        return await devices.UpsertAsync(registration, cancellationToken)
            ? DeviceRegistrationResult.Done
            : DeviceRegistrationResult.NotFound;
    }
}

// AUTO-001 §12: DELETE /api/devices/{installationId} on sign-out. Only the caller's own row is
// touched; an installation the caller has no row for (including one owned by someone else) is 404.
// Repeating it is harmless.
public sealed class UnregisterDeviceHandler(IDeviceRegistrationRepository devices, TimeProvider time)
{
    public async Task<DeviceRegistrationResult> HandleAsync(Guid userId, string? installationId, CancellationToken cancellationToken)
    {
        if (!DeviceRegistration.IsValidInstallationId(installationId))
        {
            return DeviceRegistrationResult.Invalid(new Dictionary<string, string[]>
            {
                ["installationId"] = [$"Must be {DeviceRegistration.MinInstallationIdLength}–{DeviceRegistration.MaxInstallationIdLength} characters of letters, digits, '-' or '_'."]
            });
        }

        return await devices.SignOutAsync(userId, installationId!, time.GetUtcNow(), cancellationToken)
            ? DeviceRegistrationResult.Done
            : DeviceRegistrationResult.NotFound;
    }
}
