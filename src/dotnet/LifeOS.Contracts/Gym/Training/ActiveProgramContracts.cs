namespace LifeOS.Contracts.Gym.Training;

// Activates a program for a number of cycles (1–99).
public sealed record ActivateProgramRequest(Guid ProgramId, int Cycles);

// The active program on Train: "Cycle CurrentCycle of TotalCycles". ToDo lists the current cycle's
// workouts not done yet, by position (each startable through the workout session endpoint); Done the
// ones already done in this cycle, in the order they were done.
public sealed record ActiveProgramResponse(
    Guid Id,
    Guid ProgramId,
    string ProgramName,
    int TotalCycles,
    int CurrentCycle,
    DateTimeOffset ActivatedAtUtc,
    IReadOnlyList<TrainingWorkoutResponse> ToDo,
    IReadOnlyList<CompletedCycleWorkoutResponse> Done);

// SessionId is the finished workout that counted (its History entry).
public sealed record CompletedCycleWorkoutResponse(
    Guid Id,
    string Name,
    int Position,
    Guid SessionId,
    DateTimeOffset CompletedAtUtc);
