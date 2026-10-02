using LifeOS.Application.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Application.Gym.Programs;

public enum WorkoutProgramEditStatus
{
    Updated,

    // Missing, or another user's program.
    ProgramNotFound,

    // The program has no such workout.
    WorkoutNotFound,

    // The workout has no such block.
    BlockNotFound,

    // A referenced exercise is missing or another user's.
    ExerciseNotFound
}

public sealed record WorkoutProgramEditResult(WorkoutProgramEditStatus Status, WorkoutProgramDetails? Program)
{
    public static WorkoutProgramEditResult Updated(WorkoutProgramDetails program) =>
        new(WorkoutProgramEditStatus.Updated, program);

    public static WorkoutProgramEditResult Failed(WorkoutProgramEditStatus status) => new(status, null);
}

// The shared shape of every edit inside a program: load the owner's program for update, apply one
// change through the domain, save, and return the whole updated program. A change that reports a
// failure (or throws ArgumentException for invalid input) saves nothing.
internal static class WorkoutProgramEdit
{
    public static async Task<WorkoutProgramEditResult> RunAsync(
        IWorkoutProgramRepository programRepository,
        IExerciseRepository exerciseRepository,
        Guid userId,
        Guid programId,
        Func<WorkoutProgram, Task<WorkoutProgramEditStatus>> change,
        CancellationToken cancellationToken)
    {
        var program = await programRepository.GetForUpdateAsync(userId, programId, cancellationToken);

        if (program is null)
        {
            return WorkoutProgramEditResult.Failed(WorkoutProgramEditStatus.ProgramNotFound);
        }

        var status = await change(program);

        if (status != WorkoutProgramEditStatus.Updated)
        {
            return WorkoutProgramEditResult.Failed(status);
        }

        await programRepository.SaveAsync(program, cancellationToken);

        return WorkoutProgramEditResult.Updated(
            await WorkoutProgramDetails.ReadAsync(program, exerciseRepository, cancellationToken));
    }

    public static Task<WorkoutProgramEditResult> RunAsync(
        IWorkoutProgramRepository programRepository,
        IExerciseRepository exerciseRepository,
        Guid userId,
        Guid programId,
        Func<WorkoutProgram, WorkoutProgramEditStatus> change,
        CancellationToken cancellationToken) =>
        RunAsync(
            programRepository,
            exerciseRepository,
            userId,
            programId,
            program => Task.FromResult(change(program)),
            cancellationToken);
}
