using LifeOS.Domain.Gym.Exercises;

namespace LifeOS.Domain.Gym.Programs;

// What a block prescribes for one exercise: the exercise, optional notes and the ordered sets.
// Validated by WorkoutBlock when applied.
public sealed record ExercisePrescription(Exercise Exercise, string? Notes, IReadOnlyList<RepRange> Sets);
