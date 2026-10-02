namespace LifeOS.Domain.Gym.Programs;

// One exercise of a block, referenced by the Exercise's stable id, with optional notes and its
// ordered set prescriptions (e.g. 12 / 10 / 8 or 8-10 / 8-10 / 8-10).
public sealed class WorkoutBlockExercise
{
    public const int MaxSets = 20;
    public const int MaxNotesLength = 1000;

    private readonly List<WorkoutSetPrescription> _sets = [];

    private WorkoutBlockExercise(Guid id, Guid workoutBlockId, Guid userId, Guid exerciseId, int position, string? notes)
    {
        Id = id;
        WorkoutBlockId = workoutBlockId;
        UserId = userId;
        ExerciseId = exerciseId;
        Position = position;
        Notes = notes;
    }

    public Guid Id { get; }

    public Guid WorkoutBlockId { get; }

    // The program's owner; the referenced Exercise must belong to the same user.
    public Guid UserId { get; }

    public Guid ExerciseId { get; private set; }

    // 1 for a Single block; 1 (A) or 2 (B) in a Superset.
    public int Position { get; }

    public string? Notes { get; private set; }

    // Ordered by position (set 1, set 2, ...); never empty.
    public IReadOnlyList<WorkoutSetPrescription> Sets => _sets.OrderBy(set => set.Position).ToList();

    internal static WorkoutBlockExercise Create(WorkoutBlock block, int position, ExercisePrescription prescription)
    {
        var exercise = new WorkoutBlockExercise(
            Guid.CreateVersion7(),
            block.Id,
            block.UserId,
            prescription.Exercise.Id,
            position,
            NormalizeNotes(prescription.Notes));

        exercise.ApplySets(prescription.Sets);

        return exercise;
    }

    // Callers validate first (WorkoutBlock.Update).
    internal void Apply(ExercisePrescription prescription)
    {
        ExerciseId = prescription.Exercise.Id;
        Notes = NormalizeNotes(prescription.Notes);
        ApplySets(prescription.Sets);
    }

    internal static void Validate(ExercisePrescription prescription)
    {
        NormalizeNotes(prescription.Notes);

        if (prescription.Sets is null || prescription.Sets.Count == 0)
        {
            throw new ArgumentException("At least one set is required.", "sets");
        }

        if (prescription.Sets.Count > MaxSets)
        {
            throw new ArgumentException($"At most {MaxSets} sets are allowed.", "sets");
        }

        if (prescription.Sets.Any(set => set is null))
        {
            throw new ArgumentException("Every set needs target reps.", "sets");
        }
    }

    // Set rows keep their identity by position: existing positions are updated, extra sets are
    // appended and surplus sets removed.
    private void ApplySets(IReadOnlyList<RepRange> sets)
    {
        var current = Sets;

        for (var index = 0; index < sets.Count; index++)
        {
            if (index < current.Count)
            {
                current[index].Change(sets[index]);
            }
            else
            {
                _sets.Add(WorkoutSetPrescription.Create(this, index + 1, sets[index]));
            }
        }

        foreach (var surplus in current.Skip(sets.Count))
        {
            _sets.Remove(surplus);
        }
    }

    private static string? NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var normalized = notes.Trim();

        if (normalized.Length > MaxNotesLength)
        {
            throw new ArgumentException($"Notes can have at most {MaxNotesLength} characters.", nameof(notes));
        }

        return normalized;
    }
}
