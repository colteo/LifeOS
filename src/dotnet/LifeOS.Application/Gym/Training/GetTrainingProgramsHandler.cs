using LifeOS.Application.Gym.Programs;

namespace LifeOS.Application.Gym.Training;

// A program as a choice of workouts to train: counts only, no authoring detail.
public sealed record TrainingProgram(Guid Id, string Name, IReadOnlyList<TrainingWorkout> Workouts);

public sealed record TrainingWorkout(Guid Id, string Name, int Position, int BlockCount, int ExerciseCount, int PrescribedSetCount)
{
    // WorkoutSession.Start refuses a workout without blocks: there is nothing to record.
    public bool CanStart => BlockCount > 0;
}

// The user's workouts to start, grouped by program. Read-only; starting one is the GYM-002 start.
public sealed class GetTrainingProgramsHandler
{
    private readonly IWorkoutProgramRepository _programRepository;

    public GetTrainingProgramsHandler(IWorkoutProgramRepository programRepository)
    {
        _programRepository = programRepository;
    }

    public async Task<IReadOnlyList<TrainingProgram>> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var programs = await _programRepository.GetTrainingProgramsAsync(userId, cancellationToken);

        // The Programs list order (name ignoring case, then id); workouts by position.
        return programs
            .OrderBy(program => program.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(program => program.Id)
            .Select(program => program with { Workouts = program.Workouts.OrderBy(workout => workout.Position).ToList() })
            .ToList();
    }
}
