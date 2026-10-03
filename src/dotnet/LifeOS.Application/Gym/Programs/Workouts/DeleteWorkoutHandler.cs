using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Training;

namespace LifeOS.Application.Gym.Programs.Workouts;

// Deletes one workout with its blocks and prescriptions; the remaining workouts keep their order.
// When the program is active and the remaining workouts are all done in the current cycle, the cycle
// advances in the same change (GYM-004).
public sealed class DeleteWorkoutHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;
    private readonly ActiveProgramProgress _activeProgramProgress;

    public DeleteWorkoutHandler(
        IWorkoutProgramRepository programRepository,
        IExerciseRepository exerciseRepository,
        ActiveProgramProgress activeProgramProgress)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
        _activeProgramProgress = activeProgramProgress;
    }

    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        Guid programId,
        Guid workoutId,
        CancellationToken cancellationToken) =>
        WorkoutProgramEdit.RunAsync(
            _programRepository,
            _exerciseRepository,
            userId,
            programId,
            async program =>
            {
                if (!program.RemoveWorkout(workoutId))
                {
                    return WorkoutProgramEditStatus.WorkoutNotFound;
                }

                await _activeProgramProgress.ReconcileAsync(program, cancellationToken);

                return WorkoutProgramEditStatus.Updated;
            },
            cancellationToken);
}
