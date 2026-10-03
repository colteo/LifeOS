using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Programs.Workouts;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Application.Gym.Training;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Training;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Gym;

// GYM-004 use cases: activating, reading for Train, progress through the GYM-002 workout flow,
// stopping, and template changes while active.
public class ActiveProgramHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);

    private readonly InMemoryExerciseRepository _exercises = new();
    private readonly InMemoryWorkoutProgramRepository _programs = new();
    private readonly InMemoryWorkoutSessionRepository _sessions = new();
    private readonly InMemoryActiveProgramRepository _active = new();
    private readonly ManualTimeProvider _clock = new(Now);

    // ---- Activate ----

    [Fact]
    public async Task Activate_OffersEveryWorkoutToDo_InCycleOne()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1", "Day 2", "Day 3");

        var result = await Activate().HandleAsync(TestUsers.A, program.Id, 5, CancellationToken.None);

        Assert.Equal(ActivateProgramStatus.Activated, result.Status);
        var active = result.Program!;
        Assert.Equal((program.Id, "Program", 5, 1, Now), (active.ProgramId, active.ProgramName, active.TotalCycles, active.CurrentCycle, active.ActivatedAtUtc));
        Assert.Equal(["Day 1", "Day 2", "Day 3"], active.ToDo.Select(workout => workout.Name));
        Assert.All(active.ToDo, workout => Assert.True(workout.CanStart));
        Assert.Empty(active.Done);
        Assert.Equal(active.Id, (await Get().HandleAsync(TestUsers.A, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task Activate_WhileAnotherProgramIsActive_ReportsIt()
    {
        var first = await ProgramAsync(TestUsers.A, "Day 1");
        var second = await ProgramAsync(TestUsers.A, "Day 1");
        var active = (await Activate().HandleAsync(TestUsers.A, first.Id, 3, CancellationToken.None)).Program!;

        var result = await Activate().HandleAsync(TestUsers.A, second.Id, 3, CancellationToken.None);

        Assert.Equal((ActivateProgramStatus.AnotherActive, (Guid?)active.Id), (result.Status, result.ActiveProgramId));
        Assert.Single(_active.Programs);
    }

    [Fact]
    public async Task Activate_AnotherUsersProgram_IsNotFound()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1");

        var result = await Activate().HandleAsync(TestUsers.B, program.Id, 3, CancellationToken.None);

        Assert.Equal(ActivateProgramStatus.ProgramNotFound, result.Status);
        Assert.Empty(_active.Programs);
    }

    [Fact]
    public async Task Activate_InvalidCyclesOrIncompleteProgram_Throws()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1");
        var empty = WorkoutProgram.Create(TestUsers.A, "Empty", Now);
        await _programs.AddAsync(empty, CancellationToken.None);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Activate().HandleAsync(TestUsers.A, program.Id, 0, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Activate().HandleAsync(TestUsers.A, empty.Id, 3, CancellationToken.None));
        Assert.Empty(_active.Programs);
    }

    // ---- Progress through the workout flow ----

    [Fact]
    public async Task FinishingWorkouts_MovesThemToDone_OpensTheNextCycle_AndCompletesTheProgram()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1", "Day 2", "Day 3");
        var (day1, day2, day3) = (program.Workouts[0].Id, program.Workouts[1].Id, program.Workouts[2].Id);
        await Activate().HandleAsync(TestUsers.A, program.Id, 2, CancellationToken.None);

        await TrainAsync(program.Id, day2);

        var active = (await Get().HandleAsync(TestUsers.A, CancellationToken.None))!;
        Assert.Equal(1, active.CurrentCycle);
        Assert.Equal(["Day 1", "Day 3"], active.ToDo.Select(workout => workout.Name));
        var done = Assert.Single(active.Done);
        Assert.Equal((day2, "Day 2"), (done.Id, done.Name));
        Assert.Equal(_sessions.Sessions.Single().Id, done.SessionId);

        await TrainAsync(program.Id, day3);
        await TrainAsync(program.Id, day1);

        active = (await Get().HandleAsync(TestUsers.A, CancellationToken.None))!;
        Assert.Equal(2, active.CurrentCycle);
        Assert.Equal(["Day 1", "Day 2", "Day 3"], active.ToDo.Select(workout => workout.Name));
        Assert.Empty(active.Done);

        await TrainAsync(program.Id, day1);
        await TrainAsync(program.Id, day2);
        await TrainAsync(program.Id, day3);

        Assert.Null(await Get().HandleAsync(TestUsers.A, CancellationToken.None));
        var stored = _active.Programs.Single();
        Assert.Equal((ActiveProgramStatus.Completed, 2, 6), (stored.Status, stored.CurrentCycle, stored.Completions.Count));
        Assert.Equal(6, _sessions.Sessions.Count);
    }

    [Fact]
    public async Task Done_IsInTheOrderTheWorkoutsWereFinished()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1", "Day 2", "Day 3");
        await Activate().HandleAsync(TestUsers.A, program.Id, 2, CancellationToken.None);

        await TrainAsync(program.Id, program.Workouts[2].Id);
        await TrainAsync(program.Id, program.Workouts[0].Id);

        var active = (await Get().HandleAsync(TestUsers.A, CancellationToken.None))!;
        Assert.Equal(["Day 3", "Day 1"], active.Done.Select(workout => workout.Name));
        Assert.Equal(["Day 2"], active.ToDo.Select(workout => workout.Name));
    }

    [Fact]
    public async Task ADiscardedWorkout_OrAnotherProgramsWorkout_DoesNotCount()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1", "Day 2");
        var other = await ProgramAsync(TestUsers.A, "Other");
        await Activate().HandleAsync(TestUsers.A, program.Id, 2, CancellationToken.None);

        var discarded = (await Start().HandleAsync(TestUsers.A, program.Id, program.Workouts[0].Id, CancellationToken.None)).Session!;
        await new DiscardWorkoutSessionHandler(_sessions).HandleAsync(TestUsers.A, discarded.Id, CancellationToken.None);
        await TrainAsync(other.Id, other.Workouts[0].Id);

        var active = (await Get().HandleAsync(TestUsers.A, CancellationToken.None))!;
        Assert.Equal(["Day 1", "Day 2"], active.ToDo.Select(workout => workout.Name));
        Assert.Empty(active.Done);
    }

    [Fact]
    public async Task FinishingWithoutAnActiveProgram_WorksAsBefore()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1");

        await TrainAsync(program.Id, program.Workouts[0].Id);

        Assert.Equal(Domain.Gym.Sessions.WorkoutSessionStatus.Completed, _sessions.Sessions.Single().Status);
        Assert.Empty(_active.Programs);
    }

    // ---- Stop ----

    [Fact]
    public async Task Stop_RemovesTheProgramFromTrain_AndAllowsActivatingAnother()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1");
        var next = await ProgramAsync(TestUsers.A, "Day 1");
        await Activate().HandleAsync(TestUsers.A, program.Id, 3, CancellationToken.None);

        Assert.True(await Stop().HandleAsync(TestUsers.A, CancellationToken.None));

        Assert.Null(await Get().HandleAsync(TestUsers.A, CancellationToken.None));
        Assert.False(await Stop().HandleAsync(TestUsers.A, CancellationToken.None));
        Assert.Equal(ActiveProgramStatus.Stopped, _active.Programs.Single().Status);
        Assert.Equal(ActivateProgramStatus.Activated, (await Activate().HandleAsync(TestUsers.A, next.Id, 3, CancellationToken.None)).Status);
    }

    // ---- Ownership ----

    [Fact]
    public async Task AnotherUser_SeesAndStopsNothing()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1");
        await Activate().HandleAsync(TestUsers.A, program.Id, 3, CancellationToken.None);

        Assert.Null(await Get().HandleAsync(TestUsers.B, CancellationToken.None));
        Assert.False(await Stop().HandleAsync(TestUsers.B, CancellationToken.None));
        Assert.NotNull(await Get().HandleAsync(TestUsers.A, CancellationToken.None));
    }

    // ---- Template changes ----

    [Fact]
    public async Task DeletingTheOnlyWorkoutLeftToDo_OpensTheNextCycle()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1", "Day 2");
        await Activate().HandleAsync(TestUsers.A, program.Id, 3, CancellationToken.None);
        await TrainAsync(program.Id, program.Workouts[0].Id);

        var result = await new DeleteWorkoutHandler(_programs, _exercises, Progress())
            .HandleAsync(TestUsers.A, program.Id, program.Workouts[1].Id, CancellationToken.None);

        Assert.Equal(WorkoutProgramEditStatus.Updated, result.Status);
        var active = (await Get().HandleAsync(TestUsers.A, CancellationToken.None))!;
        Assert.Equal(2, active.CurrentCycle);
        Assert.Equal(["Day 1"], active.ToDo.Select(workout => workout.Name));
    }

    [Fact]
    public async Task AWorkoutAddedWhileActive_AppearsToDo_AndAnEmptyOneIsNotStartable()
    {
        var program = await ProgramAsync(TestUsers.A, "Day 1");
        await Activate().HandleAsync(TestUsers.A, program.Id, 3, CancellationToken.None);

        await new AddWorkoutHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, "Day 2", CancellationToken.None);

        var active = (await Get().HandleAsync(TestUsers.A, CancellationToken.None))!;
        Assert.Equal([("Day 1", true), ("Day 2", false)], active.ToDo.Select(workout => (workout.Name, workout.CanStart)));
    }

    // ---- Helpers ----

    private ActivateProgramHandler Activate() => new(_active, _programs, _clock);

    private GetActiveProgramHandler Get() => new(_active, _programs);

    private StopActiveProgramHandler Stop() => new(_active, _clock);

    private ActiveProgramProgress Progress() => new(_active, _programs, _clock);

    private StartWorkoutSessionHandler Start() => new(_programs, _sessions, _exercises, _clock);

    // Starts the workout from Train and finishes it an hour later.
    private async Task TrainAsync(Guid programId, Guid workoutId)
    {
        _clock.Advance(TimeSpan.FromDays(1));
        var session = (await Start().HandleAsync(TestUsers.A, programId, workoutId, CancellationToken.None)).Session!;
        _clock.Advance(TimeSpan.FromHours(1));

        var finished = await new FinishWorkoutSessionHandler(_sessions, _exercises, Progress(), _clock)
            .HandleAsync(TestUsers.A, session.Id, CancellationToken.None);

        Assert.Equal(WorkoutSessionChangeStatus.Changed, finished.Status);
    }

    // A program whose workouts each have one Single bench block.
    private async Task<WorkoutProgram> ProgramAsync(Guid userId, params string[] workouts)
    {
        var bench = _exercises.Add(userId, "Bench press");
        var program = WorkoutProgram.Create(userId, "Program", Now);

        foreach (var name in workouts)
        {
            program.AddWorkout(name).AddBlock(WorkoutBlockKind.Single, 90, [new ExercisePrescription(bench, null, [new RepRange(8, 8)])]);
        }

        await _programs.AddAsync(program, CancellationToken.None);

        return program;
    }
}
