namespace LifeOS.Contracts.Gym.Exercises;

public sealed record CreateExerciseRequest(string Name);

public sealed record ExerciseResponse(Guid Id, string Name, DateTimeOffset CreatedAtUtc);
