namespace LifeOS.Contracts.Notifications;

// POST /api/notifications/test (AUTO-001 §12): counts only.
public sealed record TestNotificationResponse(int Devices, int Sent, int Failed);
