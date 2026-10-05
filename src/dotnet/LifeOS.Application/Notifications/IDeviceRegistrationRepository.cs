using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Notifications;

// AUTO-001 §12: device registrations, one row per (installation, user). Every write is atomic.
public interface IDeviceRegistrationRepository
{
    // The app's registration of its installation for registration.UserId, in one transaction:
    // - another user's Active row on the same installation becomes Inactive (SignedOut, token cleared):
    //   one installation has one signed-in owner;
    // - the same token on any other row becomes Inactive (TokenInvalid, token cleared): a token is
    //   never active for two rows;
    // - the caller's own row for this installation is created or updated (Active with the token, or
    //   Inactive PermissionDenied); its id and creation time are kept.
    // False when the user does not exist.
    Task<bool> UpsertAsync(DeviceRegistration registration, CancellationToken cancellationToken);

    // Sign-out: the caller's row for this installation becomes Inactive (SignedOut, token cleared)
    // and stays as delivery history. False when the caller has no row for it.
    Task<bool> SignOutAsync(Guid userId, string installationId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // The token to send to, only while the registration is Active and owned by this user.
    Task<PushTarget?> GetPushTargetAsync(Guid deviceRegistrationId, Guid userId, CancellationToken cancellationToken);

    // The provider reported the token invalid: Inactive (TokenInvalid), token cleared — only if the
    // registration still holds that token (a newer token from the app is kept).
    Task<bool> InvalidateTokenAsync(Guid deviceRegistrationId, string pushToken, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
