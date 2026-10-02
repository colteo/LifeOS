using LifeOS.Contracts.Gym.Programs;

namespace LifeOS.App.Services.Gym;

// The block editor's state: the kind (fixed), the rest and one slot per exercise (A, B for a superset),
// each with its ordered sets. Validate gives readable client-side messages; the API stays
// authoritative and re-validates everything.
public sealed class BlockDraft
{
	public const int DefaultRestSeconds = 90;
	public const int MaxSets = 20;
	public const int MaxReps = 999;

	private BlockDraft(string kind, int? restSeconds, IReadOnlyList<ExerciseSlotDraft> slots)
	{
		Kind = kind;
		RestSeconds = restSeconds;
		Slots = slots;
	}

	public string Kind { get; }

	public int? RestSeconds { get; set; }

	public IReadOnlyList<ExerciseSlotDraft> Slots { get; }

	public bool IsSuperset => Kind == GymDisplay.Superset;

	// A new block: 3 × 8 per exercise and the default rest.
	public static BlockDraft New(string kind)
	{
		var slotCount = kind == GymDisplay.Superset ? 2 : 1;

		return new BlockDraft(
			kind,
			DefaultRestSeconds,
			Enumerable.Range(1, slotCount).Select(position => ExerciseSlotDraft.New(GymDisplay.SlotLabel(kind, position))).ToList());
	}

	public static BlockDraft From(WorkoutBlockResponse block) =>
		new(
			block.Kind,
			block.RestSeconds,
			block.Exercises
				.OrderBy(exercise => exercise.Position)
				.Select(exercise => new ExerciseSlotDraft(
					GymDisplay.SlotLabel(block.Kind, exercise.Position),
					exercise.ExerciseId,
					exercise.Notes ?? string.Empty,
					exercise.Sets
						.OrderBy(set => set.Position)
						.Select(set => new SetDraft
						{
							MinReps = set.TargetMinReps,
							MaxReps = set.TargetMaxReps == set.TargetMinReps ? null : set.TargetMaxReps
						})
						.ToList()))
				.ToList());

	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();

		foreach (var slot in Slots)
		{
			var name = slot.Label is null ? "the exercise" : $"exercise {slot.Label}";

			if (slot.ExerciseId is null)
			{
				errors.Add($"Choose {name}.");
			}

			if (slot.Sets.Count == 0)
			{
				errors.Add($"Add at least one set for {name}.");
			}
			else if (slot.Sets.Count > MaxSets)
			{
				errors.Add($"Use at most {MaxSets} sets for {name}.");
			}

			for (var index = 0; index < slot.Sets.Count; index++)
			{
				var set = slot.Sets[index];

				if (set.MinReps is not { } min || min < 1 || min > MaxReps)
				{
					errors.Add($"Set {index + 1} of {name}: enter reps between 1 and {MaxReps}.");
				}
				else if (set.MaxReps is { } max && (max < min || max > MaxReps))
				{
					errors.Add($"Set {index + 1} of {name}: the maximum reps must be at least {min}.");
				}
			}
		}

		return errors;
	}

	// Call after Validate succeeded. An empty maximum means exactly the minimum.
	public IReadOnlyList<WorkoutBlockExerciseRequest> ToExercises() =>
		Slots
			.Select(slot => new WorkoutBlockExerciseRequest(
				slot.ExerciseId!.Value,
				string.IsNullOrWhiteSpace(slot.Notes) ? null : slot.Notes.Trim(),
				slot.Sets
					.Select(set => new WorkoutSetRequest(set.MinReps!.Value, set.MaxReps ?? set.MinReps!.Value))
					.ToList()))
			.ToList();
}

public sealed class ExerciseSlotDraft
{
	public ExerciseSlotDraft(string? label, Guid? exerciseId, string notes, List<SetDraft> sets)
	{
		Label = label;
		ExerciseId = exerciseId;
		Notes = notes;
		Sets = sets;
	}

	// "A" or "B" in a superset; null for a single block.
	public string? Label { get; }

	public Guid? ExerciseId { get; set; }

	public string Notes { get; set; }

	public List<SetDraft> Sets { get; }

	public static ExerciseSlotDraft New(string? label)
	{
		var slot = new ExerciseSlotDraft(label, null, string.Empty, []);
		slot.Fill(3, 8, null);

		return slot;
	}

	// The "3 sets × 8 reps" shortcut: replaces every set with the same target (a range when maxReps
	// is above minReps).
	public void Fill(int sets, int minReps, int? maxReps)
	{
		Sets.Clear();

		for (var index = 0; index < sets; index++)
		{
			Sets.Add(new SetDraft { MinReps = minReps, MaxReps = maxReps == minReps ? null : maxReps });
		}
	}

	// A new last set copies the previous one (or 8 reps for the first).
	public void AddSet()
	{
		var last = Sets.LastOrDefault();

		Sets.Add(new SetDraft { MinReps = last?.MinReps ?? 8, MaxReps = last?.MaxReps });
	}

	// The last remaining set cannot be removed: a prescription has at least one set.
	public void RemoveSet(int index)
	{
		if (Sets.Count > 1 && index >= 0 && index < Sets.Count)
		{
			Sets.RemoveAt(index);
		}
	}
}

public sealed class SetDraft
{
	public int? MinReps { get; set; }

	// Empty means exactly MinReps.
	public int? MaxReps { get; set; }
}
