namespace LifeOS.Domain.Gym.Training;

// A workout of the program done in one cycle: the finished session that counted for it.
public sealed class ActiveProgramCompletion
{
    private ActiveProgramCompletion(
        Guid id,
        Guid activeProgramId,
        Guid userId,
        int cycle,
        Guid workoutTemplateId,
        Guid workoutSessionId,
        DateTimeOffset completedAtUtc)
    {
        Id = id;
        ActiveProgramId = activeProgramId;
        UserId = userId;
        Cycle = cycle;
        WorkoutTemplateId = workoutTemplateId;
        WorkoutSessionId = workoutSessionId;
        CompletedAtUtc = completedAtUtc;
    }

    public Guid Id { get; }

    public Guid ActiveProgramId { get; }

    // The active program's owner; the database's composite keys use it as an ownership backstop.
    public Guid UserId { get; }

    // 1-based cycle the workout counted for.
    public int Cycle { get; }

    // The program's workout. Not a foreign key: a workout deleted from the program simply stops
    // counting (only the program's current workouts are required).
    public Guid WorkoutTemplateId { get; }

    public Guid WorkoutSessionId { get; }

    // When the session was finished.
    public DateTimeOffset CompletedAtUtc { get; }

    internal static ActiveProgramCompletion Create(
        ActiveProgram program,
        Guid workoutTemplateId,
        Guid workoutSessionId,
        DateTimeOffset completedAtUtc) =>
        new(Guid.CreateVersion7(), program.Id, program.UserId, program.CurrentCycle, workoutTemplateId, workoutSessionId, completedAtUtc);
}
