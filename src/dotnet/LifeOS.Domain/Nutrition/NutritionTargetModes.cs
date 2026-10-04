namespace LifeOS.Domain.Nutrition;

// How one weekday of a target plan gets its target (NUT-003). Stored by name.
public enum NutritionTargetDayMode
{
    // The plan's default target; valid only when the plan has one.
    Default,

    // The weekday's own target.
    Custom,

    // No target on that weekday.
    NoTarget
}

// A per-date exception inside a plan (NUT-003). There is no "Default" override: removing the
// override returns the date to its weekday rule.
public enum NutritionTargetOverrideMode
{
    Custom,
    NoTarget
}
