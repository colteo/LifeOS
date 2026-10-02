using LifeOS.Application.Gym.Sessions;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Gym;

public class WorkoutSessionHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    private readonly InMemoryExerciseRepository _exercises = new();
    private readonly InMemoryWorkoutProgramRepository _programs = new();
    private readonly InMemoryWorkoutSessionRepository _sessions = new();
    private readonly ManualTimeProvider _clock = new(Now);

    // ---- Start ----

    [Fact]
    public async Task Start_SnapshotsTheWorkout_WithTheServerStartTime()
    {
        var (programId, workoutId) = await ProgramAsync(TestUsers.A);

        var result = await Start().HandleAsync(TestUsers.A, programId, workoutId, CancellationToken.None);

        Assert.Equal(StartWorkoutSessionStatus.Started, result.Status);
        var session = result.Session!;
        Assert.Equal((WorkoutSessionStatus.InProgress, Now, Now), (session.Status, session.StartedAtUtc, session.ServerTimeUtc));
        Assert.Equal((programId, (Guid?)workoutId, "Push"), (session.ProgramId!.Value, session.WorkoutId, session.WorkoutName));
        Assert.Equal(["Bench press", "Row", "Curl"], session.Blocks.SelectMany(block => block.Exercises).Select(exercise => exercise.ExerciseName));
        Assert.Equal((0, 5), (session.CompletedSetCount, session.PrescribedSetCount));
        Assert.Null(session.Rest);
        Assert.Equal(TestUsers.A, _sessions.Stored(session.Id)!.UserId);
    }

    [Fact]
    public async Task Start_WhileAnotherWorkoutIsInProgress_ReportsItAndStartsNothing()
    {
        var (programId, workoutId) = await ProgramAsync(TestUsers.A);
        var first = (await Start().HandleAsync(TestUsers.A, programId, workoutId, CancellationToken.None)).Session!;

        var second = await Start().HandleAsync(TestUsers.A, programId, workoutId, CancellationToken.None);

        Assert.Equal((StartWorkoutSessionStatus.AnotherInProgress, (Guid?)first.Id), (second.Status, second.InProgressSessionId));
        Assert.Single(_sessions.Sessions);
    }

    [Fact]
    public async Task Start_LosingAConcurrentStart_ReportsTheWinner()
    {
        var (programId, workoutId) = await ProgramAsync(TestUsers.A);
        var program = _programs.Stored(programId);
        var winner = WorkoutSession.Start(program, program.FindWorkout(workoutId)!, Now);
        _sessions.BeforeAdd = () =>
        {
            _sessions.BeforeAdd = null;
            _sessions.TryAddAsync(winner, CancellationToken.None).GetAwaiter().GetResult();
        };

        var result = await Start().HandleAsync(TestUsers.A, programId, workoutId, CancellationToken.None);

        Assert.Equal((StartWorkoutSessionStatus.AnotherInProgress, (Guid?)winner.Id), (result.Status, result.InProgressSessionId));
        Assert.Single(_sessions.Sessions);
    }

    [Fact]
    public async Task Start_AnotherUsersWorkout_IsNotFound()
    {
        var (programId, workoutId) = await ProgramAsync(TestUsers.A);

        var result = await Start().HandleAsync(TestUsers.B, programId, workoutId, CancellationToken.None);

        Assert.Equal(StartWorkoutSessionStatus.WorkoutNotFound, result.Status);
        Assert.Empty(_sessions.Sessions);
    }

    [Fact]
    public async Task Start_AnUnknownWorkoutOfTheProgram_IsNotFound()
    {
        var (programId, _) = await ProgramAsync(TestUsers.A);

        var result = await Start().HandleAsync(TestUsers.A, programId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(StartWorkoutSessionStatus.WorkoutNotFound, result.Status);
    }

    [Fact]
    public async Task Start_AnEmptyWorkout_IsInvalid()
    {
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);
        var empty = program.AddWorkout("Empty");
        await _programs.AddAsync(program, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => Start().HandleAsync(TestUsers.A, program.Id, empty.Id, CancellationToken.None));
        Assert.Empty(_sessions.Sessions);
    }

    [Fact]
    public async Task Start_AfterFinishing_CreatesANewIndependentSession()
    {
        var (programId, workoutId) = await ProgramAsync(TestUsers.A);
        var first = (await Start().HandleAsync(TestUsers.A, programId, workoutId, CancellationToken.None)).Session!;
        await Finish().HandleAsync(TestUsers.A, first.Id, CancellationToken.None);

        var second = await Start().HandleAsync(TestUsers.A, programId, workoutId, CancellationToken.None);

        Assert.Equal(StartWorkoutSessionStatus.Started, second.Status);
        Assert.NotEqual(first.Id, second.Session!.Id);
        Assert.Equal(WorkoutSessionStatus.Completed, _sessions.Stored(first.Id)!.Status);
    }

    // ---- Get / current ----

    [Fact]
    public async Task Current_IsTheInProgressSession_UntilItIsFinished()
    {
        var (programId, workoutId) = await ProgramAsync(TestUsers.A);
        var started = (await Start().HandleAsync(TestUsers.A, programId, workoutId, CancellationToken.None)).Session!;

        Assert.Equal(started.Id, (await Get().HandleCurrentAsync(TestUsers.A, CancellationToken.None))!.Id);
        Assert.Null(await Get().HandleCurrentAsync(TestUsers.B, CancellationToken.None));

        await Finish().HandleAsync(TestUsers.A, started.Id, CancellationToken.None);

        Assert.Null(await Get().HandleCurrentAsync(TestUsers.A, CancellationToken.None));
        Assert.Equal(WorkoutSessionStatus.Completed, (await Get().HandleAsync(TestUsers.A, started.Id, CancellationToken.None))!.Status);
    }

    // ---- Record ----

    [Fact]
    public async Task RecordSet_UsesTheServerTime_AndReturnsTheRest()
    {
        var session = await StartedAsync(TestUsers.A);
        var set = session.Blocks[0].Exercises[0].Sets[0];
        _clock.Advance(TimeSpan.FromMinutes(3));

        var result = await Record().HandleAsync(TestUsers.A, session.Id, set.Id, 8, 80m, CancellationToken.None);

        Assert.Equal(WorkoutSessionChangeStatus.Changed, result.Status);
        var recorded = result.Session!.Blocks[0].Exercises[0].Sets[0];
        Assert.Equal((8, (decimal?)80m, (DateTimeOffset?)Now.AddMinutes(3)), (recorded.ActualReps, recorded.WeightKg, recorded.CompletedAtUtc));
        Assert.Equal(new RestPeriod(Now.AddMinutes(3), 90), result.Session.Rest);
        Assert.Equal(1, result.Session.CompletedSetCount);
    }

    [Fact]
    public async Task RecordSet_Correction_KeepsTheCompletionTime()
    {
        var session = await StartedAsync(TestUsers.A);
        var setId = session.Blocks[0].Exercises[0].Sets[0].Id;
        await Record().HandleAsync(TestUsers.A, session.Id, setId, 8, 80m, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await Record().HandleAsync(TestUsers.A, session.Id, setId, 6, null, CancellationToken.None);

        var corrected = result.Session!.Blocks[0].Exercises[0].Sets[0];
        Assert.Equal((6, (decimal?)null, (DateTimeOffset?)Now), (corrected.ActualReps, corrected.WeightKg, corrected.CompletedAtUtc));
    }

    [Fact]
    public async Task RecordSet_InvalidInput_Throws_AndStoresNothing()
    {
        var session = await StartedAsync(TestUsers.A);
        var setId = session.Blocks[0].Exercises[0].Sets[0].Id;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Record().HandleAsync(TestUsers.A, session.Id, setId, 0, null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Record().HandleAsync(TestUsers.A, session.Id, setId, 8, 0m, CancellationToken.None));

        Assert.Equal(0, _sessions.Stored(session.Id)!.CompletedSetCount);
    }

    [Fact]
    public async Task RecordSet_UnknownSet_IsNotFound()
    {
        var session = await StartedAsync(TestUsers.A);

        var result = await Record().HandleAsync(TestUsers.A, session.Id, Guid.NewGuid(), 8, null, CancellationToken.None);

        Assert.Equal(WorkoutSessionChangeStatus.SetNotFound, result.Status);
    }

    [Fact]
    public async Task RecordSet_OnACompletedSession_IsRefused()
    {
        var session = await StartedAsync(TestUsers.A);
        await Finish().HandleAsync(TestUsers.A, session.Id, CancellationToken.None);

        var result = await Record().HandleAsync(TestUsers.A, session.Id, session.Blocks[0].Exercises[0].Sets[0].Id, 8, 80m, CancellationToken.None);

        Assert.Equal(WorkoutSessionChangeStatus.AlreadyCompleted, result.Status);
        Assert.Equal(0, _sessions.Stored(session.Id)!.CompletedSetCount);
    }

    // ---- Finish ----

    [Fact]
    public async Task Finish_UsesTheServerTime_AndAllowsIncompleteSets()
    {
        var session = await StartedAsync(TestUsers.A);
        await Record().HandleAsync(TestUsers.A, session.Id, session.Blocks[0].Exercises[0].Sets[0].Id, 8, 80m, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(45));

        var result = await Finish().HandleAsync(TestUsers.A, session.Id, CancellationToken.None);

        Assert.Equal(WorkoutSessionChangeStatus.Changed, result.Status);
        Assert.Equal((WorkoutSessionStatus.Completed, (DateTimeOffset?)Now.AddMinutes(45)), (result.Session!.Status, result.Session.CompletedAtUtc));
        Assert.Equal((1, 5), (result.Session.CompletedSetCount, result.Session.PrescribedSetCount));
        Assert.Null(result.Session.Rest);
        Assert.Equal(WorkoutSessionChangeStatus.AlreadyCompleted, (await Finish().HandleAsync(TestUsers.A, session.Id, CancellationToken.None)).Status);
    }

    // ---- Discard ----

    [Fact]
    public async Task Discard_RemovesTheInProgressSession()
    {
        var session = await StartedAsync(TestUsers.A);

        Assert.Equal(WorkoutSessionChangeStatus.Changed, await Discard().HandleAsync(TestUsers.A, session.Id, CancellationToken.None));

        Assert.Null(_sessions.Stored(session.Id));
        Assert.Equal(WorkoutSessionChangeStatus.SessionNotFound, await Discard().HandleAsync(TestUsers.A, session.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Discard_ACompletedSession_IsRefused_AndKeepsIt()
    {
        var session = await StartedAsync(TestUsers.A);
        await Finish().HandleAsync(TestUsers.A, session.Id, CancellationToken.None);

        Assert.Equal(WorkoutSessionChangeStatus.AlreadyCompleted, await Discard().HandleAsync(TestUsers.A, session.Id, CancellationToken.None));
        Assert.NotNull(_sessions.Stored(session.Id));
    }

    // ---- Ownership ----

    [Fact]
    public async Task AnotherUsersSession_IsNotFound_ForEveryOperation_AndIsNotChanged()
    {
        var session = await StartedAsync(TestUsers.A);
        var setId = session.Blocks[0].Exercises[0].Sets[0].Id;

        Assert.Null(await Get().HandleAsync(TestUsers.B, session.Id, CancellationToken.None));
        Assert.Equal(WorkoutSessionChangeStatus.SessionNotFound, (await Record().HandleAsync(TestUsers.B, session.Id, setId, 8, 80m, CancellationToken.None)).Status);
        Assert.Equal(WorkoutSessionChangeStatus.SessionNotFound, (await Finish().HandleAsync(TestUsers.B, session.Id, CancellationToken.None)).Status);
        Assert.Equal(WorkoutSessionChangeStatus.SessionNotFound, await Discard().HandleAsync(TestUsers.B, session.Id, CancellationToken.None));

        var stored = _sessions.Stored(session.Id)!;
        Assert.Equal((WorkoutSessionStatus.InProgress, 0), (stored.Status, stored.CompletedSetCount));
    }

    [Fact]
    public async Task EachUser_MayHaveTheirOwnInProgressWorkout()
    {
        await StartedAsync(TestUsers.A);

        var ofB = await StartedAsync(TestUsers.B);

        Assert.Equal(TestUsers.B, _sessions.Stored(ofB.Id)!.UserId);
        Assert.Equal(2, _sessions.Sessions.Count);
    }

    // ---- Helpers ----

    // A program whose "Push" workout has a Single bench block (3 × 8, rest 90) and a Superset of
    // row and curl (1 set each, rest 120).
    private async Task<(Guid ProgramId, Guid WorkoutId)> ProgramAsync(Guid userId)
    {
        var bench = _exercises.Add(userId, "Bench press");
        var row = _exercises.Add(userId, "Row");
        var curl = _exercises.Add(userId, "Curl");
        var program = WorkoutProgram.Create(userId, "Program", Now);
        var workout = program.AddWorkout("Push");
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(bench, 3, 8)]);
        workout.AddBlock(WorkoutBlockKind.Superset, 120, [Prescription(row, 1, 10), Prescription(curl, 1, 12)]);
        await _programs.AddAsync(program, CancellationToken.None);

        return (program.Id, workout.Id);
    }

    private async Task<WorkoutSessionDetails> StartedAsync(Guid userId)
    {
        var (programId, workoutId) = await ProgramAsync(userId);

        return (await Start().HandleAsync(userId, programId, workoutId, CancellationToken.None)).Session!;
    }

    private static ExercisePrescription Prescription(Exercise exercise, int sets, int reps) =>
        new(exercise, null, Enumerable.Repeat(new RepRange(reps, reps), sets).ToList());

    private StartWorkoutSessionHandler Start() => new(_programs, _sessions, _exercises, _clock);

    private GetWorkoutSessionHandler Get() => new(_sessions, _exercises, _clock);

    private RecordWorkoutSetHandler Record() => new(_sessions, _exercises, _clock);

    private FinishWorkoutSessionHandler Finish() => new(_sessions, _exercises, _clock);

    private DiscardWorkoutSessionHandler Discard() => new(_sessions);
}
