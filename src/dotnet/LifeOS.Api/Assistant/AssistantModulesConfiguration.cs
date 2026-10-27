namespace LifeOS.Api.Assistant;

// AI-008: which modules beyond Finance the assistant's conversation offers. Water intakes and meals are approved
// for development and experimentation only (ADR-014 addendum), so they default to on in Development and off
// everywhere else; Assistant:Modules:Hydration and Assistant:Modules:NutritionMeals set them explicitly.
public sealed record AssistantModulesOptions(bool Hydration, bool NutritionMeals);

public static class AssistantModulesConfiguration
{
    public const string Section = "Assistant:Modules";

    public static AssistantModulesOptions Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(Section);
        var development = environment.IsDevelopment();

        return new AssistantModulesOptions(
            Flag(section, "Hydration", development),
            Flag(section, "NutritionMeals", development));
    }

    private static bool Flag(IConfigurationSection section, string name, bool fallback) =>
        section[name] is { } value
            ? bool.TryParse(value, out var enabled) ? enabled : throw new InvalidOperationException($"{Section}:{name} must be true or false.")
            : fallback;
}
