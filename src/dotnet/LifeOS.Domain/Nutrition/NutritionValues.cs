namespace LifeOS.Domain.Nutrition;

// The nutrition of one meal (NUT-002): energy and the three macronutrients, nothing else.
// Values are non-negative, bounded to what one meal can plausibly contain, and kept to one decimal
// place so that daily totals are exact decimal sums; anything more precise would be false precision.
public sealed record NutritionValues
{
    public const decimal MaxCaloriesKcal = 10000m;
    public const decimal MaxMacroGrams = 1000m;
    public const int Decimals = 1;

    private NutritionValues(decimal caloriesKcal, decimal proteinGrams, decimal carbsGrams, decimal fatGrams)
    {
        CaloriesKcal = caloriesKcal;
        ProteinGrams = proteinGrams;
        CarbsGrams = carbsGrams;
        FatGrams = fatGrams;
    }

    public decimal CaloriesKcal { get; }

    public decimal ProteinGrams { get; }

    public decimal CarbsGrams { get; }

    public decimal FatGrams { get; }

    // Parameter names are the request field names, so validation failures can name the field.
    public static NutritionValues Create(decimal caloriesKcal, decimal proteinGrams, decimal carbsGrams, decimal fatGrams) =>
        new(Normalize(caloriesKcal, MaxCaloriesKcal, "kcal", nameof(caloriesKcal)),
            Normalize(proteinGrams, MaxMacroGrams, "g", nameof(proteinGrams)),
            Normalize(carbsGrams, MaxMacroGrams, "g", nameof(carbsGrams)),
            Normalize(fatGrams, MaxMacroGrams, "g", nameof(fatGrams)));

    private static decimal Normalize(decimal value, decimal maximum, string unit, string field)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(field, "Nutrition values cannot be negative.");
        }

        var rounded = Math.Round(value, Decimals, MidpointRounding.AwayFromZero);

        if (rounded > maximum)
        {
            throw new ArgumentOutOfRangeException(field, $"Must be at most {maximum:0} {unit}.");
        }

        return rounded;
    }
}
