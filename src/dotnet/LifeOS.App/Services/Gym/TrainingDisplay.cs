using LifeOS.Contracts.Gym.Programs;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.App.Services.Gym;

// The Train page's presentation of the active program, and whether a program can be activated. The
// API is authoritative (it refuses an empty workout, a second workout in progress, a second active
// program and a program that cannot be completed). Plain .NET.
public static class TrainingDisplay
{
	// The default number of cycles offered when activating a program.
	public const int DefaultCycles = 4;

	public const int MaxCycles = 99;

	// "Cycle 2 of 5".
	public static string Cycle(ActiveProgramResponse program) =>
		$"Cycle {program.CurrentCycle} of {program.TotalCycles}";

	// "1 of 3 workouts done".
	public static string CycleProgress(ActiveProgramResponse program)
	{
		var total = program.ToDo.Count + program.Done.Count;

		return $"{program.Done.Count} of {Count(total, "workout")} done";
	}

	// "5 exercises · 15 sets", or why the workout cannot be started.
	public static string Summary(TrainingWorkoutResponse workout) =>
		workout.CanStart
			? $"{Count(workout.ExerciseCount, "exercise")} · {Count(workout.PrescribedSetCount, "set")}"
			: "No exercises yet";

	// One workout in progress at a time: while one is, nothing else is started.
	public static bool CanStartNow(TrainingWorkoutResponse workout, bool workoutInProgress) =>
		workout.CanStart && !workoutInProgress;

	// Why the program cannot be activated (every cycle must be completable), or null when it can.
	public static string? ActivationBlocker(WorkoutProgramResponse program) =>
		program.Workouts.Count == 0 ? "Add a workout before activating the program."
		: program.Workouts.Any(workout => workout.Blocks.Count == 0) ? "Every workout needs exercises before activating the program."
		: null;

	// A number of cycles the API accepts.
	public static bool IsValidCycles(int cycles) => cycles is >= 1 and <= MaxCycles;

	private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
