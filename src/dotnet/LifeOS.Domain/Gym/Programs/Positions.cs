namespace LifeOS.Domain.Gym.Programs;

// Ordering within a program: positions are 1-based and contiguous among siblings.
internal static class Positions
{
    public static void Renumber<T>(IEnumerable<T> ordered, Action<T, int> moveTo)
    {
        var position = 1;

        foreach (var item in ordered)
        {
            moveTo(item, position++);
        }
    }

    // The siblings in the requested order. The request must be a permutation of the current ids, so a
    // stale or partial list is rejected instead of silently producing a different order.
    public static IReadOnlyList<T> Arrange<T>(
        IReadOnlyCollection<T> items,
        Func<T, Guid> id,
        IReadOnlyList<Guid>? orderedIds,
        string parameterName,
        string itemName)
    {
        var byId = items.ToDictionary(id);

        if (orderedIds is null
            || orderedIds.Count != byId.Count
            || orderedIds.Distinct().Count() != orderedIds.Count
            || !orderedIds.All(byId.ContainsKey))
        {
            throw new ArgumentException($"The new order must list every {itemName} exactly once.", parameterName);
        }

        return orderedIds.Select(itemId => byId[itemId]).ToList();
    }
}
