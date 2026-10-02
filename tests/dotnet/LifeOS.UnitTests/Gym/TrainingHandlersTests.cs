using LifeOS.Application.Gym.Sessions;
using LifeOS.Application.Gym.Training;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Gym;

// Choosing a workout to train: the read-only training query, and that its choices start through the
// GYM-002 start.
public class TrainingHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    private readonly InMemoryExerciseRepository _exercises = new();
    private readonly InMemoryWorkoutProgramRepository _programs = new();
    private readonly InMemoryWorkoutSessionRepository _sessions = new();

    [Fact]
    public async Task Programs_AreOrderedByName_WithWorkoutsByPosition_AndCounts()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        var curl = _exercises.Add(TestUsers.A, "Curl");

        var strength = WorkoutProgram.Create(TestUsers.A, "strength", Now);
        strength.AddWorkout("Upper A").AddBlock(WorkoutBlockKind.Single, 180, [Prescription(bench, 5)]);

        var hypertrophy = WorkoutProgram.Create(TestUsers.A, "Hypertrophy", Now);
        var push = hypertrophy.AddWorkout("Push");
        push.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(bench, 3)]);
        push.AddBlock(WorkoutBlockKind.Superset, 120, [Prescription(row, 3), Prescription(curl, 2)]);
        var pull = hypertrophy.AddWorkout("Pull");
        hypertrophy.ReorderWorkouts([pull.Id, push.Id]);

        var empty = WorkoutProgram.Create(TestUsers.A, "New program", Now);

        await _programs.AddAsync(strength, CancellationToken.None);
        await _programs.AddAsync(hypertrophy, CancellationToken.None);
        await _programs.AddAsync(empty, CancellationToken.None);

        var programs = await Handler().HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Equal(["Hypertrophy", "New program", "strength"], programs.Select(program => program.Name));
        Assert.Equal(
            [("Pull", 1, 0, 0, 0, false), ("Push", 2, 2, 3, 8, true)],
            programs[0].Workouts.Select(workout => (workout.Name, workout.Position, workout.BlockCount, workout.ExerciseCount, workout.PrescribedSetCount, workout.CanStart)));
        // A program without workouts is still returned, so the app can tell it from no program.
        Assert.Empty(programs[1].Workouts);
        Assert.Equal([("Upper A", true)], programs[2].Workouts.Select(workout => (workout.Name, workout.CanStart)));
    }

    [Fact]
    public async Task AnotherUsersPrograms_NeverAppear()
    {
        var other = WorkoutProgram.Create(TestUsers.B, "B's program", Now);
        other.AddWorkout("B's workout").AddBlock(WorkoutBlockKind.Single, 90, [Prescription(_exercises.Add(TestUsers.B, "Squat"), 3)]);
        await _programs.AddAsync(other, CancellationToken.None);
        await _programs.AddAsync(WorkoutProgram.Create(TestUsers.A, "A's program", Now), CancellationToken.None);

        var programs = await Handler().HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Equal(["A's program"], programs.Select(program => program.Name));
        Assert.Equal(["B's program"], (await Handler().HandleAsync(TestUsers.B, CancellationToken.None)).Select(program => program.Name));
    }

    [Fact]
    public async Task Choices_StartThroughTheWorkoutStart_ExceptAWorkoutThatCannotStart()
    {
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);
        program.AddWorkout("Push").AddBlock(WorkoutBlockKind.Single, 90, [Prescription(_exercises.Add(TestUsers.A, "Bench press"), 3)]);
        program.AddWorkout("Empty");
        await _programs.AddAsync(program, CancellationToken.None);
        var choices = (await Handler().HandleAsync(TestUsers.A, CancellationToken.None)).Single();
        var (ready, notReady) = (choices.Workouts[0], choices.Workouts[1]);
        var start = new StartWorkoutSessionHandler(_programs, _sessions, _exercises, new ManualTimeProvider(Now));

        Assert.False(notReady.CanStart);
        await Assert.ThrowsAsync<ArgumentException>(() => start.HandleAsync(TestUsers.A, choices.Id, notReady.Id, CancellationToken.None));

        Assert.True(ready.CanStart);
        var started = await start.HandleAsync(TestUsers.A, choices.Id, ready.Id, CancellationToken.None);
        Assert.Equal((StartWorkoutSessionStatus.Started, "Push", ready.PrescribedSetCount), (started.Status, started.Session!.WorkoutName, started.Session.PrescribedSetCount));
    }

    private GetTrainingProgramsHandler Handler() => new(_programs);

    private static ExercisePrescription Prescription(Exercise exercise, int sets) =>
        new(exercise, null, Enumerable.Repeat(new RepRange(8, 8), sets).ToList());
}
