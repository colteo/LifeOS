namespace LifeOS.Contracts.Devices;

// PUT /api/devices/{installationId} (AUTO-001 §12). The owner is the signed-in user, never a field.
// PushToken is required when NotificationsPermitted is true and must be omitted when it is false.
public sealed record RegisterDeviceRequest(string? Platform, string? PushToken, bool? NotificationsPermitted);
