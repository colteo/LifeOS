using LifeOS.Contracts.Gym.Sessions;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.App.Services.Gym;

// What the Home training card shows (APP-001), from the current workout session and the active
// program. The next workout is only a suggestion: Train still lets the athlete start any workout of
// the cycle, in any order, and the API stays authoritative. Plain .NET.
public static class HomeTraining
{
	// A workout in progress wins: it is resumed, and nothing else is offered for starting.
	public static HomeTrainingState StateOf(WorkoutSessionResponse? current, ActiveProgramResponse? active) =>
		current is not null ? HomeTrainingState.InProgress
		: active is not null ? HomeTrainingState.NextWorkout
		: HomeTrainingState.NoProgram;

	// The first workout still to do in the current cycle, in program order; null when none is left.
	public static TrainingWorkoutResponse? NextWorkout(ActiveProgramResponse active) =>
		active.ToDo.OrderBy(workout => workout.Position).FirstOrDefault();

	// "Cycle 2 of 5" for the workout in progress when it belongs to the active program; otherwise null.
	public static string? InProgressCycle(WorkoutSessionResponse current, ActiveProgramResponse? active) =>
		active is not null && current.ProgramId == active.ProgramId ? TrainingDisplay.Cycle(active) : null;
}

public enum HomeTrainingState
{
	InProgress,
	NextWorkout,
	NoProgram
}
