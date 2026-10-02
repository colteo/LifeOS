using System.Globalization;
using LifeOS.Contracts.Gym.Sessions;

namespace LifeOS.App.Services.Gym;

// The active workout's presentation: sets grouped in execution order, input parsing and prefills.
// The API is authoritative (validation, rest, timestamps). Plain .NET.
public static class WorkoutSessionDisplay
{
	public const string InProgress = "InProgress";
	public const string Completed = "Completed";

	// One set of a block in execution order. Label is "A1"/"B2" in a superset, "Set 1" otherwise.
	public sealed record SetRow(
		string Label,
		WorkoutSessionExerciseResponse Exercise,
		WorkoutSessionSetResponse Set,
		bool EndsRound);

	// Rounds by set position, A before B within a round. With different set counts a round holds only
	// the sets that exist (A = 4, B = 3: A1 B1, A2 B2, A3 B3, A4). EndsRound marks the last set of a
	// round, after which the prescribed rest follows.
	public static IReadOnlyList<SetRow> Rows(WorkoutSessionBlockResponse block)
	{
		var superset = block.Kind == GymDisplay.Superset;

		var ordered = block.Exercises
			.SelectMany(exercise => exercise.Sets.Select(set => (Exercise: exercise, Set: set)))
			.OrderBy(item => item.Set.Position)
			.ThenBy(item => item.Exercise.Position)
			.ToList();

		return ordered
			.Select((item, index) => new SetRow(
				superset ? $"{GymDisplay.SlotLabel(block.Kind, item.Exercise.Position)}{item.Set.Position}" : $"Set {item.Set.Position}",
				item.Exercise,
				item.Set,
				index == ordered.Count - 1 || ordered[index + 1].Set.Position != item.Set.Position))
			.ToList();
	}

	// The first pending set in natural order (blocks, then rounds, A before B); null when all are done.
	public static Guid? NextSetId(WorkoutSessionResponse session) =>
		session.Blocks
			.OrderBy(block => block.Position)
			.SelectMany(Rows)
			.FirstOrDefault(row => row.Set.CompletedAtUtc is null)?
			.Set.Id;

	// What the inputs of a set start with: its recorded values once completed; otherwise the last
	// values recorded for the same exercise earlier in this workout, or the target's minimum reps.
	public static (string Weight, string Reps) Prefill(WorkoutSessionExerciseResponse exercise, WorkoutSessionSetResponse set)
	{
		if (set.CompletedAtUtc is not null)
		{
			return (FormatWeight(set.WeightKg), set.ActualReps?.ToString(CultureInfo.InvariantCulture) ?? "");
		}

		var previous = exercise.Sets
			.Where(candidate => candidate.CompletedAtUtc is not null && candidate.Position < set.Position)
			.OrderBy(candidate => candidate.Position)
			.LastOrDefault();

		return previous is null
			? ("", set.TargetMinReps.ToString(CultureInfo.InvariantCulture))
			: (FormatWeight(previous.WeightKg), previous.ActualReps?.ToString(CultureInfo.InvariantCulture) ?? "");
	}

	// Reps: a whole number from 1 to 999.
	public static bool TryParseReps(string? text, out int reps, out string? error)
	{
		error = null;

		if (int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out reps) && reps is >= 1 and <= 999)
		{
			return true;
		}

		error = "Enter the reps done (1–999).";
		return false;
	}

	// Weight in kg: empty for bodyweight (null); otherwise "82,5" or "82.5", more than 0, at most
	// 1000, with at most two decimals.
	public static bool TryParseWeight(string? text, out decimal? weightKg, out string? error)
	{
		weightKg = null;
		error = null;

		if (string.IsNullOrWhiteSpace(text))
		{
			return true;
		}

		var normalized = text.Trim().Replace(',', '.');

		if (normalized.Count(character => character == '.') <= 1
			&& decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
			&& value > 0
			&& value <= 1000
			&& decimal.Round(value, 2) == value)
		{
			weightKg = value;
			return true;
		}

		error = "Enter the weight in kg (e.g. 82.5), or leave it empty for bodyweight.";
		return false;
	}

	// "82.5", "100"; empty for bodyweight.
	public static string FormatWeight(decimal? weightKg) =>
		weightKg?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";

	// "82.5 kg × 8" or "BW × 12".
	public static string Performed(WorkoutSessionSetResponse set) =>
		set.WeightKg is { } weight
			? $"{FormatWeight(weight)} kg × {set.ActualReps}"
			: $"BW × {set.ActualReps}";

	public static string Target(WorkoutSessionSetResponse set) =>
		$"{GymDisplay.Reps(set.TargetMinReps, set.TargetMaxReps)} reps";

	public static string Progress(int completed, int prescribed) => $"{completed} / {prescribed} sets";
}
