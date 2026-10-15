namespace LifeOS.Contracts.Reminders;

// REM-001 §9. Enum-like values are strings:
//   kind: "Custom"
//   schedule kind: "Once", "Daily", "Weekly"
//   weekdays: "Monday" ... "Sunday"
//   status: "Active", "Paused", "Completed"
//   last outcome: "Notified", "Missed", "Disabled"
// Times are local wall-clock "HH:mm" (24-hour) and dates yyyy-MM-dd, in the user's time zone.
// NextDueLocal is "yyyy-MM-ddTHH:mm" with no offset by design: a wall-clock time that follows the
// device's zone. UtcOffsetMinutes is the device's current offset from UTC (e.g. 120 for UTC+02:00); the
// API derives "now" from it. There is no user id: the owner is always the authenticated user.

// Date only for "Once", Weekdays only for "Weekly".
public sealed record ReminderScheduleRequest(string? Kind, DateOnly? Date, IReadOnlyList<string>? Weekdays, string? Time);

// Kind: optional, "Custom" (the only kind in v1).
public sealed record CreateReminderRequest(
    string? Kind,
    string? Title,
    string? Note,
    ReminderScheduleRequest? Schedule,
    int? UtcOffsetMinutes);

// Title, note and schedule are replaced; the kind cannot change.
public sealed record UpdateReminderRequest(string? Title, string? Note, ReminderScheduleRequest? Schedule, int? UtcOffsetMinutes);

// Body of resume.
public sealed record ResumeReminderRequest(int? UtcOffsetMinutes);

// 15, 60 or 180.
public sealed record SnoozeReminderRequest(int? Minutes);

public sealed record ReminderScheduleResponse(string Kind, DateOnly? Date, IReadOnlyList<string>? Weekdays, string Time);

// Label: what the list shows (the title for Custom reminders). InsideQuietHours: the time falls inside
// the user's quiet hours, so the push waits until they end.
public sealed record ReminderResponse(
    Guid Id,
    string Kind,
    string Label,
    string Title,
    string? Note,
    ReminderScheduleResponse Schedule,
    string Status,
    string? NextDueLocal,
    DateTimeOffset? SnoozeUntilUtc,
    DateTimeOffset? LastTriggeredAtUtc,
    string? LastOutcome,
    bool InsideQuietHours);

// TimeZoneKnown: false until the device reported its zone (nothing is due before). PushAvailable: false
// when push is not configured on the server.
public sealed record RemindersResponse(
    bool TimeZoneKnown,
    bool PushAvailable,
    IReadOnlyList<ReminderResponse> Upcoming,
    IReadOnlyList<ReminderResponse> Paused,
    IReadOnlyList<ReminderResponse> Completed);
