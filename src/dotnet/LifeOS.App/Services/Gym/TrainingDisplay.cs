using LifeOS.Contracts.Gym.Training;

namespace LifeOS.App.Services.Gym;

// The Train page's presentation: which workouts to offer and whether each can be started now.
// The API is authoritative (it refuses an empty workout and a second workout in progress). Plain .NET.
public static class TrainingDisplay
{
	public enum Availability
	{
		NoPrograms,

		// Programs exist, but none has a workout.
		NoWorkouts,

		Workouts
	}

	public static Availability Of(IReadOnlyList<TrainingProgramResponse> programs) =>
		programs.Count == 0 ? Availability.NoPrograms
		: programs.All(program => program.Workouts.Count == 0) ? Availability.NoWorkouts
		: Availability.Workouts;

	// Programs without workouts offer nothing to train.
	public static IReadOnlyList<TrainingProgramResponse> Choices(IReadOnlyList<TrainingProgramResponse> programs) =>
		programs.Where(program => program.Workouts.Count > 0).ToList();

	// "5 exercises · 15 sets", or why the workout cannot be started.
	public static string Summary(TrainingWorkoutResponse workout) =>
		workout.CanStart
			? $"{Count(workout.ExerciseCount, "exercise")} · {Count(workout.PrescribedSetCount, "set")}"
			: "No exercises yet";

	// One workout in progress at a time: while one is, nothing else is started.
	public static bool CanStartNow(TrainingWorkoutResponse workout, bool workoutInProgress) =>
		workout.CanStart && !workoutInProgress;

	private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
