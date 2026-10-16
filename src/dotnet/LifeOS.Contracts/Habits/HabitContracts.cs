namespace LifeOS.Contracts.Habits;

// HAB-001 §8. Enum-like values are strings:
//   frequency kind: "Daily", "Weekdays", "TimesPerWeek" ("Inactive" only in responses: archived period)
//   weekdays: "Monday" ... "Sunday"
//   completion source: "Manual", "Hydration" ("Reach my water target": done when the day's water target
//                      is reached; read from Hydration, never checked in as Done)
//   habit status: "Active", "Archived"
//   check-in status: "Done", "Excused"
//   day status: "Done", "Excused", "Missed", "Pending", "NotScheduled", "Inactive"
//   streak unit: "Day", "Week"
//   list group: "ToDo", "Done", "NotToday"
// Hydration (water habits only): the date's water total and target read through from Hydration
// (HYD-001); Habits stores neither. Added after Android 1.7.0, which ignores it.
// Dates are local calendar dates (yyyy-MM-dd). UtcOffsetMinutes is the device's current offset from UTC
// (e.g. 120 for UTC+02:00); the API derives the user's "today" from it. There is no user id: the owner
// is always the authenticated user.

public sealed record HabitFrequencyRequest(string? Kind, IReadOnlyList<string>? Weekdays, int? TimesPerWeek);

// StartsOn: optional, default today; within a year of today.
public sealed record CreateHabitRequest(
    string? Name,
    string? Description,
    string? CompletionSource,
    HabitFrequencyRequest? Frequency,
    DateOnly? StartsOn,
    int? UtcOffsetMinutes);

// Name and description are replaced. Frequency: optional; a change applies from today (409
// habit_archived while the habit is archived).
public sealed record UpdateHabitRequest(string? Name, string? Description, HabitFrequencyRequest? Frequency, int? UtcOffsetMinutes);

// Body of archive and restore.
public sealed record HabitTodayRequest(int? UtcOffsetMinutes);

public sealed record SetHabitCheckInRequest(string? Status, string? Note, int? UtcOffsetMinutes);

// Weekdays only for "Weekdays", TimesPerWeek only for "TimesPerWeek".
public sealed record HabitFrequencyResponse(string Kind, IReadOnlyList<string>? Weekdays, int? TimesPerWeek, DateOnly EffectiveFrom);

// Percent is null when nothing was expected yet ("Not enough data yet").
public sealed record HabitAdherenceResponse(int Met, int Expected, int? Percent, int WindowDays);

// An ISO week (Monday-Sunday) of a times-per-week habit. Done includes extras ("4 / 3").
public sealed record HabitWeekResponse(DateOnly WeekStart, int Done, int Target);

public sealed record HabitProgressResponse(
    DateOnly AsOf,
    string TodayStatus,
    int CurrentStreak,
    int BestStreak,
    string StreakUnit,
    HabitAdherenceResponse Adherence,
    HabitWeekResponse? ThisWeek);

public sealed record HabitDayItemResponse(DateOnly Date, string Status, string? Note);

// TargetMl is null when the date has no water target (the water habit's day is then "NotScheduled").
public sealed record HabitHydrationResponse(int TotalMl, int? TargetMl);

// Frequency: the schedule in effect today ("Inactive" while archived); LastActiveFrequency: the latest
// one that is not Inactive. RecentDays: the current ISO week and the four before it, up to today.
public sealed record HabitDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    string CompletionSource,
    string Status,
    DateOnly StartsOn,
    HabitFrequencyResponse Frequency,
    HabitFrequencyResponse? LastActiveFrequency,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ArchivedAtUtc,
    HabitProgressResponse Progress,
    IReadOnlyList<HabitDayItemResponse> RecentDays,
    HabitHydrationResponse? Hydration = null);

// The day after a check-in or an undo, with the habit's updated statistics.
public sealed record HabitDayResponse(Guid HabitId, DateOnly Date, string Status, string? Note, HabitProgressResponse Progress,
    HabitHydrationResponse? Hydration = null);

public sealed record HabitHistoryResponse(Guid HabitId, DateOnly From, DateOnly To, IReadOnlyList<HabitDayItemResponse> Days);

// One row of the day list. Status, Note, Frequency and ThisWeek are for the viewed date; CurrentStreak
// is as of today.
public sealed record HabitListItemResponse(
    Guid Id,
    string Name,
    string CompletionSource,
    string Status,
    string? Note,
    string Group,
    int CurrentStreak,
    string StreakUnit,
    HabitFrequencyResponse Frequency,
    HabitWeekResponse? ThisWeek,
    HabitHydrationResponse? Hydration = null);

public sealed record UpcomingHabitResponse(Guid Id, string Name, DateOnly StartsOn);

// Habits: active habits started on or before Date, in creation order. Today: the user's local today.
public sealed record HabitsForDayResponse(
    DateOnly Date,
    DateOnly Today,
    IReadOnlyList<HabitListItemResponse> Habits,
    IReadOnlyList<UpcomingHabitResponse> Upcoming,
    int UpcomingCount,
    int ArchivedCount);

public sealed record ArchivedHabitResponse(Guid Id, string Name, string? Description, DateTimeOffset ArchivedAtUtc, HabitFrequencyResponse? LastActiveFrequency);

// Most recently archived first.
public sealed record ArchivedHabitsResponse(IReadOnlyList<ArchivedHabitResponse> Habits);
