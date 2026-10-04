using System.Reflection;
using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;

namespace LifeOS.UnitTests.Fakes;

// Mirrors the EF Core repository: scoped by user, one state per (user, effective date), a save on an
// existing day replaces its values in place (keeping id and creation time), nothing is ever deleted.
internal sealed class InMemoryNutritionTargetRepository : INutritionTargetRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<NutritionTarget> States { get; } = [];

    public int Saves { get; private set; }

    public Task<NutritionTarget?> GetEffectiveAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var state = States
                .Where(target => target.UserId == userId && target.EffectiveFrom <= date)
                .OrderByDescending(target => target.EffectiveFrom)
                .FirstOrDefault();

            return Task.FromResult(state is null ? null : Clone(state));
        }
    }

    public Task SaveAsync(NutritionTarget target, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Saves++;
            var index = States.FindIndex(stored => stored.UserId == target.UserId && stored.EffectiveFrom == target.EffectiveFrom);

            if (index < 0)
            {
                States.Add(Clone(target));
            }
            else
            {
                // ON CONFLICT DO UPDATE: the stored row keeps its id and creation time.
                var stored = States[index];
                var replacement = Clone(target);
                Set(replacement, nameof(NutritionTarget.Id), stored.Id);
                Set(replacement, nameof(NutritionTarget.CreatedAtUtc), stored.CreatedAtUtc);
                States[index] = replacement;
            }
        }

        return Task.CompletedTask;
    }

    private static NutritionTarget Clone(NutritionTarget target) => (NutritionTarget)CloneMethod.Invoke(target, null)!;

    private static void Set(NutritionTarget target, string property, object value) =>
        typeof(NutritionTarget).GetProperty(property)!.SetValue(target, value);
}
