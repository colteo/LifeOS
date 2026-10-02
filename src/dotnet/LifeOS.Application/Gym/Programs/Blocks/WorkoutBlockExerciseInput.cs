using LifeOS.Application.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Application.Gym.Programs.Blocks;

// One exercise slot of a block, in order (A, then B for a superset).
public sealed record WorkoutBlockExerciseInput(Guid ExerciseId, string? Notes, IReadOnlyList<SetTargetInput>? Sets);

// One prescribed set, in order. 8 reps is 8-8; a range is e.g. 8-10.
public sealed record SetTargetInput(int TargetMinReps, int TargetMaxReps);

internal static class ExercisePrescriptions
{
    // The domain prescriptions for the inputs, or null when any referenced exercise is missing or is
    // another user's. Throws ArgumentException for invalid reps or a missing list.
    public static async Task<IReadOnlyList<ExercisePrescription>?> ResolveAsync(
        Guid userId,
        IReadOnlyList<WorkoutBlockExerciseInput>? inputs,
        IExerciseRepository exerciseRepository,
        CancellationToken cancellationToken)
    {
        if (inputs is null || inputs.Any(input => input is null))
        {
            throw new ArgumentException("Every exercise of the block is required.", "exercises");
        }

        var exerciseIds = inputs.Select(input => input.ExerciseId).Distinct().ToList();

        // Scoped to the user: another user's exercise is reported exactly like a missing one.
        var exercises = (await exerciseRepository.GetByIdsAsync(userId, exerciseIds, cancellationToken))
            .ToDictionary(exercise => exercise.Id);

        if (exerciseIds.Any(id => !exercises.ContainsKey(id)))
        {
            return null;
        }

        return inputs
            .Select(input => new ExercisePrescription(
                exercises[input.ExerciseId],
                input.Notes,
                (input.Sets ?? []).Select(ToRepRange).ToList()))
            .ToList();
    }

    private static RepRange ToRepRange(SetTargetInput? set) =>
        set is null
            ? throw new ArgumentException("Every set needs target reps.", "sets")
            : new RepRange(set.TargetMinReps, set.TargetMaxReps);
}
