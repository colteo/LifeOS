namespace LifeOS.Domain.Nutrition;

// Who decided a daily target state (NUT-003). Stored by name, so later origins (for example an AI
// proposal the user accepted, or values from a professional) can be added without changing existing
// rows. Only Manual exists today: the user types the targets themselves.
public enum NutritionTargetSource
{
    Manual
}
