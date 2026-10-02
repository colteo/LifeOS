using LifeOS.Contracts.Gym.Programs;

namespace LifeOS.App.Services.Gym;

// Display text for workout prescriptions. Presentation only: the API is authoritative.
public static class GymDisplay
{
	public const string Single = "Single";
	public const string Superset = "Superset";

	// The practical rest choices offered by the block editor, in seconds.
	public static readonly IReadOnlyList<int> RestChoices = [30, 45, 60, 75, 90, 120, 150, 180, 240, 300];

	// "8" or "8–10".
	public static string Reps(int min, int max) => min == max ? $"{min}" : $"{min}–{max}";

	// "3 × 8" when every set has the same target, otherwise each set in order: "12 / 10 / 8".
	public static string Sets(IReadOnlyList<WorkoutSetResponse> sets)
	{
		if (sets.Count == 0)
		{
			return "No sets";
		}

		var first = sets[0];

		return sets.All(set => set.TargetMinReps == first.TargetMinReps && set.TargetMaxReps == first.TargetMaxReps)
			? $"{sets.Count} × {Reps(first.TargetMinReps, first.TargetMaxReps)}"
			: string.Join(" / ", sets.Select(set => Reps(set.TargetMinReps, set.TargetMaxReps)));
	}

	// "45 s", "2 min", "1:30 min".
	public static string Duration(int seconds) =>
		seconds < 60 ? $"{seconds} s"
		: seconds % 60 == 0 ? $"{seconds / 60} min"
		: $"{seconds / 60}:{seconds % 60:00} min";

	public static string Rest(int? seconds) => seconds is { } value ? $"Rest {Duration(value)}" : "No rest set";

	// A and B identify the two exercises of a superset; a single block has no label.
	public static string? SlotLabel(string kind, int position) =>
		kind == Superset ? (position == 1 ? "A" : "B") : null;

	public static string BlockCount(int count) => count == 1 ? "1 exercise block" : $"{count} exercise blocks";

	public static string WorkoutCount(int count) => count == 1 ? "1 workout" : $"{count} workouts";

	// The ids with the item at index moved one place up (-1) or down (+1); null when it cannot move.
	public static IReadOnlyList<Guid>? Move(IReadOnlyList<Guid> ids, int index, int direction)
	{
		var target = index + direction;

		if (index < 0 || index >= ids.Count || target < 0 || target >= ids.Count)
		{
			return null;
		}

		var moved = ids.ToList();
		(moved[index], moved[target]) = (moved[target], moved[index]);

		return moved;
	}
}
