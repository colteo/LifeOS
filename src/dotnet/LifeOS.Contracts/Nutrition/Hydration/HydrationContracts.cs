namespace LifeOS.Contracts.Nutrition.Hydration;

// HYD-001 §8. Amounts are whole millilitres. Date is the diary day (yyyy-MM-dd); Time is the local
// wall-clock time; UtcOffsetMinutes is the device's offset from UTC for that date and time (e.g. 120 for
// UTC+02:00). A date after the caller's local today is rejected.
public sealed record AddHydrationIntakeRequest(int? AmountMl, DateOnly? Date, TimeOnly? Time, int? UtcOffsetMinutes);

// The intake stays on its diary day; only the amount and time change.
public sealed record UpdateHydrationIntakeRequest(int? AmountMl, TimeOnly? Time);

public sealed record HydrationIntakeResponse(
    Guid Id,
    int AmountMl,
    DateOnly Date,
    TimeOnly Time,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

// One day's total. TargetMl and ProgressPercent are null when the day has no target; ProgressPercent may
// exceed 100, and IsTargetMet is false without a target.
public sealed record HydrationDaySummaryResponse(DateOnly Date, int TotalMl, int? TargetMl, bool IsTargetMet, int? ProgressPercent);

// One diary day with its intakes, newest first.
public sealed record HydrationDayResponse(HydrationDaySummaryResponse Day, IReadOnlyList<HydrationIntakeResponse> Intakes);

// After an add or edit: the intake and its day's updated total.
public sealed record HydrationIntakeMutationResponse(HydrationIntakeResponse Intake, HydrationDaySummaryResponse Day);

// AverageTotalMl is the mean over every day of the range, empty days included.
public sealed record HydrationRangeSummaryResponse(int DaysWithTarget, int DaysMet, int AverageTotalMl);

public sealed record HydrationRangeResponse(IReadOnlyList<HydrationDaySummaryResponse> Days, HydrationRangeSummaryResponse Summary);

// TargetMl null removes the target. Either way the change applies from the caller's local today; past
// days keep their target.
public sealed record SetHydrationTargetRequest(int? TargetMl, int? UtcOffsetMinutes);

// Exactly three amounts (ml, 1-4000 each, duplicates allowed).
public sealed record SetHydrationQuickAddRequest(IReadOnlyList<int>? AmountsMl, int? UtcOffsetMinutes);

// The target on Date (null: none) and the date that target took effect, with the quick-add amounts
// (150 / 250 / 500 until the user changes them).
public sealed record HydrationSettingsResponse(DateOnly Date, int? TargetMl, DateOnly? TargetEffectiveFrom, IReadOnlyList<int> QuickAddMl);
