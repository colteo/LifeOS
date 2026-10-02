namespace LifeOS.Domain.Gym.Programs;

// An ordered unit of a workout: a Single exercise or a Superset of two. Rest belongs to the block,
// not to an exercise: for a superset it is the rest after completing both exercises of a round, and
// nothing is prescribed between A and B. GYM-001 only stores the prescription; no timer runs.
public sealed class WorkoutBlock
{
    public const int MaxRestSeconds = 3600;

    private readonly List<WorkoutBlockExercise> _exercises = [];

    private WorkoutBlock(Guid id, Guid workoutTemplateId, Guid userId, int position, WorkoutBlockKind kind, int? restSeconds)
    {
        Id = id;
        WorkoutTemplateId = workoutTemplateId;
        UserId = userId;
        Position = position;
        Kind = kind;
        RestSeconds = restSeconds;
    }

    public Guid Id { get; }

    public Guid WorkoutTemplateId { get; }

    // The program's owner; the database's composite keys use it as an ownership backstop.
    public Guid UserId { get; }

    // 1-based order within the workout.
    public int Position { get; private set; }

    // Fixed at creation: to change the kind, the block is replaced.
    public WorkoutBlockKind Kind { get; }

    // Rest after each set (Single) or each round (Superset); null when not prescribed.
    public int? RestSeconds { get; private set; }

    // Ordered by position: exactly one for a Single block, A (1) and B (2) for a Superset.
    public IReadOnlyList<WorkoutBlockExercise> Exercises => _exercises.OrderBy(exercise => exercise.Position).ToList();

    public static int RequiredExerciseCount(WorkoutBlockKind kind) => kind == WorkoutBlockKind.Superset ? 2 : 1;

    internal static WorkoutBlock Create(
        WorkoutTemplate workout,
        int position,
        WorkoutBlockKind kind,
        int? restSeconds,
        IReadOnlyList<ExercisePrescription> exercises)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Block kind is not supported.");
        }

        ValidateRest(restSeconds);
        Validate(kind, workout.UserId, exercises);

        var block = new WorkoutBlock(Guid.CreateVersion7(), workout.Id, workout.UserId, position, kind, restSeconds);

        for (var index = 0; index < exercises.Count; index++)
        {
            block._exercises.Add(WorkoutBlockExercise.Create(block, index + 1, exercises[index]));
        }

        return block;
    }

    // Replaces the prescription while keeping the kind and the identities of the existing exercise
    // and set rows: the exercise in slot A stays slot A. Everything is validated before anything
    // changes.
    public void Update(int? restSeconds, IReadOnlyList<ExercisePrescription> exercises)
    {
        ValidateRest(restSeconds);
        Validate(Kind, UserId, exercises);

        RestSeconds = restSeconds;

        var current = Exercises;

        for (var index = 0; index < current.Count; index++)
        {
            current[index].Apply(exercises[index]);
        }
    }

    internal void MoveTo(int position)
    {
        Position = position;
    }

    private static void ValidateRest(int? restSeconds)
    {
        if (restSeconds is { } seconds && (seconds < 1 || seconds > MaxRestSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(restSeconds),
                restSeconds,
                $"Rest must be between 1 and {MaxRestSeconds} seconds.");
        }
    }

    private static void Validate(WorkoutBlockKind kind, Guid userId, IReadOnlyList<ExercisePrescription>? exercises)
    {
        var required = RequiredExerciseCount(kind);

        if (exercises is null || exercises.Count != required || exercises.Any(exercise => exercise is null))
        {
            throw new ArgumentException(
                kind == WorkoutBlockKind.Superset
                    ? "A superset must have exactly two exercises."
                    : "A single block must have exactly one exercise.",
                nameof(exercises));
        }

        foreach (var exercise in exercises)
        {
            if (exercise.Exercise is null || exercise.Exercise.UserId != userId)
            {
                throw new ArgumentException("Each exercise must be one of the program owner's exercises.", nameof(exercises));
            }

            WorkoutBlockExercise.Validate(exercise);
        }
    }
}
