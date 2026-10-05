namespace LifeOS.Domain.Nutrition;

// One daily target (NUT-003): any combination of energy and the three macronutrients. Used for a
// plan's default target, a custom weekday and a custom daily override.
// Null means that metric is not targeted. A present value is a real target, so it must be more than
// zero; it is kept to one decimal place and bounded to a plausible daily amount. At least one metric
// must be present: "no target" is an explicit mode (NoTarget), never an empty target.
public sealed record NutritionTargetValues
{
    public const decimal MaxCaloriesKcal = 10000m;
    public const decimal MaxMacroGrams = 1000m;
    public const int Decimals = 1;

    public const string NoneMessage = "Enter at least one target.";

    private NutritionTargetValues(decimal? caloriesKcal, decimal? proteinGrams, decimal? carbsGrams, decimal? fatGrams)
    {
        CaloriesKcal = caloriesKcal;
        ProteinGrams = proteinGrams;
        CarbsGrams = carbsGrams;
        FatGrams = fatGrams;
    }

    public decimal? CaloriesKcal { get; }

    public decimal? ProteinGrams { get; }

    public decimal? CarbsGrams { get; }

    public decimal? FatGrams { get; }

    // Parameter names are the request field names, so validation failures can name the field.
    public static NutritionTargetValues Create(decimal? caloriesKcal, decimal? proteinGrams, decimal? carbsGrams, decimal? fatGrams)
    {
        if (caloriesKcal is null && proteinGrams is null && carbsGrams is null && fatGrams is null)
        {
            throw new ArgumentException(NoneMessage);
        }

        return new(Normalize(caloriesKcal, MaxCaloriesKcal, "kcal", nameof(caloriesKcal)),
            Normalize(proteinGrams, MaxMacroGrams, "g", nameof(proteinGrams)),
            Normalize(carbsGrams, MaxMacroGrams, "g", nameof(carbsGrams)),
            Normalize(fatGrams, MaxMacroGrams, "g", nameof(fatGrams)));
    }

    // Stored columns back to values: null when every metric is null.
    public static NutritionTargetValues? FromStored(decimal? caloriesKcal, decimal? proteinGrams, decimal? carbsGrams, decimal? fatGrams) =>
        caloriesKcal is null && proteinGrams is null && carbsGrams is null && fatGrams is null
            ? null
            : Create(caloriesKcal, proteinGrams, carbsGrams, fatGrams);

    private static decimal? Normalize(decimal? value, decimal maximum, string unit, string field)
    {
        if (value is not { } amount)
        {
            return null;
        }

        var rounded = Math.Round(amount, Decimals, MidpointRounding.AwayFromZero);

        if (rounded <= 0)
        {
            throw new ArgumentOutOfRangeException(field, "Must be more than 0, or left empty.");
        }

        if (rounded > maximum)
        {
            throw new ArgumentOutOfRangeException(field, $"Must be at most {maximum:0} {unit}.");
        }

        return rounded;
    }
}
