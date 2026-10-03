using LifeOS.Application.Gym.History;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Gym;

public class WorkoutHistoryHandlersTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 9, 1, 18, 0, 0, TimeSpan.Zero);

    private readonly InMemoryExerciseRepository _exercises = new();
    private readonly InMemoryWorkoutProgramRepository _programs = new();
    private readonly InMemoryWorkoutSessionRepository _sessions = new();
    private readonly ManualTimeProvider _clock = new(Day1.AddDays(30));

    // ---- History list ----

    [Fact]
    public async Task History_ListsOnlyCompletedSessions_NewestCompletedFirst_FromTheSnapshot()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        var older = await CompletedAsync(TestUsers.A, "Push", Day1, TimeSpan.FromMinutes(45), (bench, [(80m, 8), (80m, 8)]), (row, [(60m, 10)]));
        var newer = await CompletedAsync(TestUsers.A, "Pull", Day1.AddDays(2), TimeSpan.FromMinutes(52), (row, [(60m, 10)]));
        await InProgressAsync(TestUsers.A, "Legs", Day1.AddDays(3), (bench, []));

        var page = await History().HandleAsync(TestUsers.A, null, 20, CancellationToken.None);

        Assert.Equal([newer.Id, older.Id], page.Items.Select(item => item.Id));
        Assert.Null(page.Next);
        var item = page.Items[1];
        Assert.Equal(("Program", "Push"), (item.ProgramName, item.WorkoutName));
        Assert.Equal((Day1, Day1.AddMinutes(45)), (item.StartedAtUtc, item.CompletedAtUtc));
        Assert.Equal(TimeSpan.FromMinutes(45), item.CompletedAtUtc - item.StartedAtUtc);
        // Bench 3 prescribed (2 done) + row 3 prescribed (1 done); 2 exercises.
        Assert.Equal((3, 6, 2), (item.CompletedSetCount, item.PrescribedSetCount, item.ExerciseCount));
    }

    [Fact]
    public async Task History_PagesAreBoundedAndDeterministic_WithTiesOrderedById()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var sessions = new List<WorkoutSession>();
        for (var day = 0; day < 4; day++)
        {
            sessions.Add(await CompletedAsync(TestUsers.A, $"W{day}", Day1.AddDays(day), TimeSpan.FromMinutes(30), (bench, [(80m, 8)])));
        }

        // Two workouts completed at the same instant: the larger id comes first.
        sessions.Add(await CompletedAsync(TestUsers.A, "Tie", Day1.AddDays(3), TimeSpan.FromMinutes(30), (bench, [(80m, 8)])));
        var expected = sessions
            .OrderByDescending(session => session.CompletedAtUtc)
            .ThenByDescending(session => session.Id)
            .Select(session => session.Id)
            .ToList();

        var first = await History().HandleAsync(TestUsers.A, null, 2, CancellationToken.None);
        var second = await History().HandleAsync(TestUsers.A, first.Next, 2, CancellationToken.None);
        var third = await History().HandleAsync(TestUsers.A, second.Next, 2, CancellationToken.None);

        Assert.Equal((2, 2, 1), (first.Items.Count, second.Items.Count, third.Items.Count));
        Assert.NotNull(second.Next);
        Assert.Null(third.Next);
        Assert.Equal(expected, first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id));
        Assert.Equal(new WorkoutHistoryCursor(first.Items[1].CompletedAtUtc, first.Items[1].Id), first.Next);
    }

    [Fact]
    public async Task History_AWorkoutFinishedWhilePaging_DoesNotShiftLaterPages()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var a = await CompletedAsync(TestUsers.A, "A", Day1, TimeSpan.FromMinutes(30), (bench, [(80m, 8)]));
        var b = await CompletedAsync(TestUsers.A, "B", Day1.AddDays(1), TimeSpan.FromMinutes(30), (bench, [(80m, 8)]));
        var first = await History().HandleAsync(TestUsers.A, null, 1, CancellationToken.None);

        await CompletedAsync(TestUsers.A, "New", Day1.AddDays(5), TimeSpan.FromMinutes(30), (bench, [(80m, 8)]));
        var second = await History().HandleAsync(TestUsers.A, first.Next, 1, CancellationToken.None);

        Assert.Equal([b.Id], first.Items.Select(item => item.Id));
        Assert.Equal([a.Id], second.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task History_PageSizeOutOfRange_IsRejected(int pageSize)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => History().HandleAsync(TestUsers.A, null, pageSize, CancellationToken.None));
    }

    [Fact]
    public async Task History_IsOwnerScoped()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var session = await CompletedAsync(TestUsers.A, "Push", Day1, TimeSpan.FromMinutes(30), (bench, [(80m, 8)]));

        Assert.Empty((await History().HandleAsync(TestUsers.B, null, 20, CancellationToken.None)).Items);
        Assert.Null(await Detail().HandleAsync(TestUsers.B, session.Id, CancellationToken.None));
    }

    [Fact]
    public async Task History_SurvivesRenamingAndDeletingTheSourceProgramAndWorkout()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Day1);
        var workout = program.AddWorkout("Push");
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(bench, 3, 8)]);
        await _programs.AddAsync(program, CancellationToken.None);
        var session = WorkoutSession.Start(program, workout, Day1);
        session.RecordSet(session.ExecutionOrder[0].Id, 8, 80m, Day1.AddMinutes(2));
        session.Finish(Day1.AddMinutes(30));
        await _sessions.TryAddAsync(session, CancellationToken.None);

        program.Rename("Renamed");
        workout.Rename("Renamed workout");
        Assert.True(await _programs.DeleteAsync(TestUsers.A, program.Id, CancellationToken.None));

        var item = Assert.Single((await History().HandleAsync(TestUsers.A, null, 20, CancellationToken.None)).Items);
        Assert.Equal(("Program", "Push"), (item.ProgramName, item.WorkoutName));
        var detail = (await Detail().HandleAsync(TestUsers.A, session.Id, CancellationToken.None))!;
        Assert.Equal(("Program", "Push", (int?)90), (detail.ProgramName, detail.WorkoutName, detail.Blocks[0].RestSeconds));
        Assert.Equal([(8, 8), (8, 8), (8, 8)], detail.Blocks[0].Exercises[0].Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps)));
    }

    // ---- History detail ----

    [Fact]
    public async Task Detail_IsTheCompletedSnapshot_WithIncompleteSetsVisible()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        var curl = _exercises.Add(TestUsers.A, "Curl");
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Day1);
        var workout = program.AddWorkout("Push");
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(bench, 2, 8)]);
        workout.AddBlock(WorkoutBlockKind.Superset, 120, [Prescription(row, 1, 10), Prescription(curl, 1, 12)]);
        var session = WorkoutSession.Start(program, workout, Day1);
        session.RecordSet(session.ExecutionOrder[0].Id, 8, 82.5m, Day1.AddMinutes(2));
        session.RecordSet(session.Blocks[1].Exercises[1].Sets[0].Id, 12, null, Day1.AddMinutes(9));
        session.Finish(Day1.AddMinutes(40));
        await _sessions.TryAddAsync(session, CancellationToken.None);

        var detail = (await Detail().HandleAsync(TestUsers.A, session.Id, CancellationToken.None))!;

        Assert.Equal((WorkoutSessionStatus.Completed, Day1, (DateTimeOffset?)Day1.AddMinutes(40)), (detail.Status, detail.StartedAtUtc, detail.CompletedAtUtc));
        Assert.Equal((2, 4), (detail.CompletedSetCount, detail.PrescribedSetCount));
        Assert.Equal([WorkoutBlockKind.Single, WorkoutBlockKind.Superset], detail.Blocks.Select(block => block.Kind));
        Assert.Equal(["Row", "Curl"], detail.Blocks[1].Exercises.Select(exercise => exercise.ExerciseName));
        Assert.Equal([(int?)8, null], detail.Blocks[0].Exercises[0].Sets.Select(set => set.ActualReps));
        Assert.Equal((decimal?)82.5m, detail.Blocks[0].Exercises[0].Sets[0].WeightKg);
        Assert.Null(detail.Blocks[0].Exercises[0].Sets[1].CompletedAtUtc);
        Assert.Null(detail.Blocks[1].Exercises[0].Sets[0].ActualReps);
        Assert.Equal((12, (decimal?)null), (detail.Blocks[1].Exercises[1].Sets[0].ActualReps!.Value, detail.Blocks[1].Exercises[1].Sets[0].WeightKg));
        Assert.Null(detail.Rest);
    }

    [Fact]
    public async Task Detail_OfAnInProgressOrUnknownSession_IsNotHistory()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var current = await InProgressAsync(TestUsers.A, "Push", Day1, (bench, [(80m, 8)]));

        Assert.Null(await Detail().HandleAsync(TestUsers.A, current.Id, CancellationToken.None));
        Assert.Null(await Detail().HandleAsync(TestUsers.A, Guid.NewGuid(), CancellationToken.None));
    }

    // ---- Previous performance ----

    [Fact]
    public async Task Previous_IsTheLatestCompletedSessionWithTheExercise_WithItsRecordedValues()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        await CompletedAsync(TestUsers.A, "Old", Day1, TimeSpan.FromMinutes(30), (bench, [(70m, 8), (70m, 8), (70m, 8)]));
        var last = await CompletedAsync(TestUsers.A, "Push", Day1.AddDays(3), TimeSpan.FromMinutes(40), (bench, [(82.5m, 8), (82.5m, 8), (82.5m, 7)]));
        var current = await InProgressAsync(TestUsers.A, "Push", Day1.AddDays(7), (bench, []));

        var previous = (await Previous().HandleAsync(TestUsers.A, current.Id, CancellationToken.None))!;

        Assert.Equal(current.Id, previous.SessionId);
        var exercise = Assert.Single(previous.Exercises);
        Assert.Equal((bench.Id, last.Id, "Push", Day1.AddDays(3).AddMinutes(40)), (exercise.ExerciseId, exercise.SessionId, exercise.WorkoutName, exercise.CompletedAtUtc));
        Assert.Equal(
            [new PreviousSet(1, 1, 8, 82.5m), new PreviousSet(1, 2, 8, 82.5m), new PreviousSet(1, 3, 7, 82.5m)],
            exercise.Sets);
    }

    [Fact]
    public async Task Previous_ExcludesTheCurrentSessionsOwnSets()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var current = await InProgressAsync(TestUsers.A, "Push", Day1, (bench, [(80m, 8), (80m, 8)]));

        var previous = (await Previous().HandleAsync(TestUsers.A, current.Id, CancellationToken.None))!;

        Assert.Empty(previous.Exercises);
    }

    [Fact]
    public async Task Previous_IgnoresInProgressSessions_AndSessionsCompletedAfterThisOneStarted()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var completed = await CompletedAsync(TestUsers.A, "Done", Day1, TimeSpan.FromMinutes(30), (bench, [(70m, 8)]));
        await InProgressAsync(TestUsers.A, "Unfinished", Day1.AddDays(1), (bench, [(90m, 8)]));
        var viewed = await CompletedAsync(TestUsers.A, "Viewed", Day1.AddDays(2), TimeSpan.FromMinutes(30), (bench, [(75m, 8)]));
        await CompletedAsync(TestUsers.A, "Later", Day1.AddDays(4), TimeSpan.FromMinutes(30), (bench, [(85m, 8)]));

        var previous = (await Previous().HandleAsync(TestUsers.A, viewed.Id, CancellationToken.None))!;

        var exercise = Assert.Single(previous.Exercises);
        Assert.Equal((completed.Id, 70m), (exercise.SessionId, exercise.Sets[0].WeightKg!.Value));
    }

    [Fact]
    public async Task Previous_OrdersCandidatesByCompletionTime()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        // Started first but completed last: the most recently COMPLETED wins.
        var longOne = await CompletedAsync(TestUsers.A, "Long", Day1, TimeSpan.FromHours(5), (bench, [(80m, 8)]));
        await CompletedAsync(TestUsers.A, "Short", Day1.AddHours(1), TimeSpan.FromMinutes(30), (bench, [(70m, 8)]));
        var current = await InProgressAsync(TestUsers.A, "Push", Day1.AddDays(1), (bench, []));

        var previous = (await Previous().HandleAsync(TestUsers.A, current.Id, CancellationToken.None))!;

        Assert.Equal(longOne.Id, Assert.Single(previous.Exercises).SessionId);
    }

    [Fact]
    public async Task Previous_IsOwnerScoped()
    {
        var benchA = _exercises.Add(TestUsers.A, "Bench press");
        var benchB = _exercises.Add(TestUsers.B, "Bench press");
        await CompletedAsync(TestUsers.B, "Push", Day1, TimeSpan.FromMinutes(30), (benchB, [(100m, 5)]));
        var current = await InProgressAsync(TestUsers.A, "Push", Day1.AddDays(1), (benchA, []));
        var others = await InProgressAsync(TestUsers.B, "Push", Day1.AddDays(1), (benchB, []));

        Assert.Empty((await Previous().HandleAsync(TestUsers.A, current.Id, CancellationToken.None))!.Exercises);
        // Another user's session is indistinguishable from a missing one.
        Assert.Null(await Previous().HandleAsync(TestUsers.A, others.Id, CancellationToken.None));
        Assert.Null(await Previous().HandleAsync(TestUsers.A, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Previous_MatchesOnlyTheSameExercise_AndOmitsExercisesWithoutHistory()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        var curl = _exercises.Add(TestUsers.A, "Curl");
        await CompletedAsync(TestUsers.A, "Pull", Day1, TimeSpan.FromMinutes(30), (row, [(60m, 10)]), (curl, [(15m, 12)]));
        var current = await InProgressAsync(TestUsers.A, "Push", Day1.AddDays(1), (bench, []), (curl, []));

        var previous = (await Previous().HandleAsync(TestUsers.A, current.Id, CancellationToken.None))!;

        Assert.Equal([curl.Id], previous.Exercises.Select(exercise => exercise.ExerciseId));
    }

    [Fact]
    public async Task Previous_KeepsFewerOrMoreSetsAndBodyweight_AsRecorded_InOneRead()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var pullUp = _exercises.Add(TestUsers.A, "Pull-up");
        // Previous: bench 5 sets prescribed, 2nd skipped; pull-ups 2 bodyweight sets.
        await CompletedAsync(TestUsers.A, "Old", Day1, TimeSpan.FromMinutes(30),
            (bench, [(80m, 8), null, (80m, 6), (80m, 6), (80m, 5)]),
            (pullUp, [(null, 12), (null, 10)]));
        var current = await InProgressAsync(TestUsers.A, "Push", Day1.AddDays(1), (pullUp, []), (bench, []));
        var reads = _sessions.PreviousPerformanceReads;

        var previous = (await Previous().HandleAsync(TestUsers.A, current.Id, CancellationToken.None))!;

        Assert.Equal(reads + 1, _sessions.PreviousPerformanceReads);
        // In the current workout's order.
        Assert.Equal([pullUp.Id, bench.Id], previous.Exercises.Select(exercise => exercise.ExerciseId));
        Assert.Equal([new PreviousSet(2, 1, 12, null), new PreviousSet(2, 2, 10, null)], previous.Exercises[0].Sets);
        // The skipped set is not performance; the others keep their real set number.
        Assert.Equal([1, 3, 4, 5], previous.Exercises[1].Sets.Select(set => set.Position));
    }

    [Fact]
    public async Task Previous_KeepsEveryOccurrenceOfAnExercise_InExecutionOrder()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        await CompletedAsync(TestUsers.A, "Old", Day1, TimeSpan.FromMinutes(30),
            (bench, [(100m, 3), (100m, 3)]),
            (row, [(60m, 10)]),
            (bench, [(70m, 10)]));
        var current = await InProgressAsync(TestUsers.A, "Push", Day1.AddDays(1), (bench, []));

        var previous = (await Previous().HandleAsync(TestUsers.A, current.Id, CancellationToken.None))!;

        Assert.Equal(
            [new PreviousSet(1, 1, 3, 100m), new PreviousSet(1, 2, 3, 100m), new PreviousSet(3, 1, 10, 70m)],
            Assert.Single(previous.Exercises).Sets);
    }

    // ---- Exercise history (UI-001) ----

    [Fact]
    public async Task ExerciseHistory_IsTheCompletedWorkoutsWithTheExercise_NewestFirst_WithEveryOccurrence()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        var othersBench = _exercises.Add(TestUsers.B, "Bench press");
        var older = await CompletedAsync(TestUsers.A, "Push", Day1, TimeSpan.FromMinutes(30), (bench, [(80m, 8), (80m, 7)]));
        await CompletedAsync(TestUsers.A, "Pull", Day1.AddDays(1), TimeSpan.FromMinutes(30), (row, [(60m, 10)]));
        // Bench twice (blocks 1 and 3), one set left pending, then bodyweight.
        var newer = await CompletedAsync(TestUsers.A, "Upper", Day1.AddDays(2), TimeSpan.FromMinutes(30),
            (bench, [(85m, 5), null, (85m, 4)]), (row, [(60m, 10)]), (bench, [(null, 12)]));
        await CompletedAsync(TestUsers.B, "Other", Day1.AddDays(3), TimeSpan.FromMinutes(30), (othersBench, [(150m, 1)]));
        var current = await InProgressAsync(TestUsers.A, "Today", Day1.AddDays(4), (bench, [(90m, 5)]), (row, []));

        var page = (await ExerciseHistory().HandleAsync(TestUsers.A, current.Id, bench.Id, null, 5, CancellationToken.None))!;

        Assert.Equal(bench.Id, page.ExerciseId);
        Assert.Equal([newer.Id, older.Id], page.Items.Select(item => item.SessionId));
        Assert.Null(page.Next);
        var latest = page.Items[0];
        Assert.Equal(("Program", "Upper", Day1.AddDays(2).AddMinutes(30)), (latest.ProgramName, latest.WorkoutName, latest.CompletedAtUtc));
        Assert.Equal(
            [new PreviousSet(1, 1, 5, 85m), new PreviousSet(1, 3, 4, 85m), new PreviousSet(3, 1, 12, null)],
            latest.Sets);
        Assert.Equal([new PreviousSet(1, 1, 8, 80m), new PreviousSet(1, 2, 7, 80m)], page.Items[1].Sets);
    }

    [Fact]
    public async Task ExerciseHistory_ExcludesTheSessionItself_AndWorkoutsCompletedAfterItStarted()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var earlier = await CompletedAsync(TestUsers.A, "Earlier", Day1, TimeSpan.FromMinutes(30), (bench, [(70m, 8)]));
        var viewed = await CompletedAsync(TestUsers.A, "Viewed", Day1.AddDays(1), TimeSpan.FromMinutes(30), (bench, [(75m, 8)]));
        await CompletedAsync(TestUsers.A, "Later", Day1.AddDays(2), TimeSpan.FromMinutes(30), (bench, [(80m, 8)]));

        var page = (await ExerciseHistory().HandleAsync(TestUsers.A, viewed.Id, bench.Id, null, 5, CancellationToken.None))!;

        Assert.Equal([earlier.Id], page.Items.Select(item => item.SessionId));
        Assert.Empty((await ExerciseHistory().HandleAsync(TestUsers.A, earlier.Id, bench.Id, null, 5, CancellationToken.None))!.Items);
    }

    [Fact]
    public async Task ExerciseHistory_PagesByKeyset_NewestFirst_WithTiesOrderedById()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var sessions = new List<WorkoutSession>();
        for (var day = 0; day < 5; day++)
        {
            sessions.Add(await CompletedAsync(TestUsers.A, $"W{day}", Day1.AddDays(day), TimeSpan.FromMinutes(30), (bench, [(80m, 8)])));
        }

        sessions.Add(await CompletedAsync(TestUsers.A, "Tie", Day1.AddDays(4), TimeSpan.FromMinutes(30), (bench, [(80m, 8)])));
        var current = await InProgressAsync(TestUsers.A, "Today", Day1.AddDays(9), (bench, []));
        var expected = sessions
            .OrderByDescending(session => session.CompletedAtUtc)
            .ThenByDescending(session => session.Id)
            .Select(session => session.Id)
            .ToList();

        var pages = new List<ExerciseHistoryPage>();
        WorkoutHistoryCursor? cursor = null;
        do
        {
            var page = (await ExerciseHistory().HandleAsync(TestUsers.A, current.Id, bench.Id, cursor, 4, CancellationToken.None))!;
            pages.Add(page);
            cursor = page.Next;
        }
        while (cursor is not null);

        Assert.Equal([4, 2], pages.Select(page => page.Items.Count));
        Assert.Equal(expected, pages.SelectMany(page => page.Items).Select(item => item.SessionId));
        Assert.Equal(new WorkoutHistoryCursor(pages[0].Items[^1].CompletedAtUtc, pages[0].Items[^1].SessionId), pages[0].Next);
    }

    [Fact]
    public async Task ExerciseHistory_IsNull_ForAnotherUsersSession_AMissingOne_OrAnExerciseNotInTheSession()
    {
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        await CompletedAsync(TestUsers.A, "Pull", Day1, TimeSpan.FromMinutes(30), (row, [(60m, 10)]));
        var current = await InProgressAsync(TestUsers.A, "Today", Day1.AddDays(1), (bench, []));

        Assert.Null(await ExerciseHistory().HandleAsync(TestUsers.B, current.Id, bench.Id, null, 5, CancellationToken.None));
        Assert.Null(await ExerciseHistory().HandleAsync(TestUsers.A, Guid.NewGuid(), bench.Id, null, 5, CancellationToken.None));
        // Row has history, but it is not part of this session.
        Assert.Null(await ExerciseHistory().HandleAsync(TestUsers.A, current.Id, row.Id, null, 5, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(GetExerciseHistoryHandler.MaxPageSize + 1)]
    public async Task ExerciseHistory_RejectsAPageSizeOutOfRange(int pageSize)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ExerciseHistory().HandleAsync(TestUsers.A, Guid.NewGuid(), Guid.NewGuid(), null, pageSize, CancellationToken.None));
    }

    // ---- Helpers ----

    // A workout of Single blocks, one per (exercise, sets); a set value is (weight, reps) to record or
    // null to leave pending. Each exercise gets max(recorded count, 3) prescribed sets.
    private async Task<WorkoutSession> CompletedAsync(
        Guid userId,
        string workoutName,
        DateTimeOffset startedAt,
        TimeSpan duration,
        params (Exercise Exercise, (decimal? WeightKg, int Reps)?[] Sets)[] blocks)
    {
        var session = Started(userId, workoutName, startedAt, blocks);
        session.Finish(startedAt + duration);
        Assert.True(await _sessions.TryAddAsync(session, CancellationToken.None));

        return session;
    }

    private async Task<WorkoutSession> InProgressAsync(
        Guid userId,
        string workoutName,
        DateTimeOffset startedAt,
        params (Exercise Exercise, (decimal? WeightKg, int Reps)?[] Sets)[] blocks)
    {
        var session = Started(userId, workoutName, startedAt, blocks);
        Assert.True(await _sessions.TryAddAsync(session, CancellationToken.None));

        return session;
    }

    private static WorkoutSession Started(
        Guid userId,
        string workoutName,
        DateTimeOffset startedAt,
        (Exercise Exercise, (decimal? WeightKg, int Reps)?[] Sets)[] blocks)
    {
        var program = WorkoutProgram.Create(userId, "Program", startedAt);
        var workout = program.AddWorkout(workoutName);

        foreach (var (exercise, sets) in blocks)
        {
            workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(exercise, Math.Max(sets.Length, 3), 8)]);
        }

        var session = WorkoutSession.Start(program, workout, startedAt);

        for (var index = 0; index < blocks.Length; index++)
        {
            var sets = session.Blocks[index].Exercises[0].Sets;

            for (var position = 0; position < blocks[index].Sets.Length; position++)
            {
                if (blocks[index].Sets[position] is { } value)
                {
                    session.RecordSet(sets[position].Id, value.Reps, value.WeightKg, startedAt.AddMinutes(1));
                }
            }
        }

        return session;
    }

    private static ExercisePrescription Prescription(Exercise exercise, int sets, int reps) =>
        new(exercise, null, Enumerable.Repeat(new RepRange(reps, reps), sets).ToList());

    private GetWorkoutHistoryHandler History() => new(_sessions);

    private GetWorkoutHistoryDetailHandler Detail() => new(_sessions, _exercises, _clock);

    private GetPreviousPerformanceHandler Previous() => new(_sessions);

    private GetExerciseHistoryHandler ExerciseHistory() => new(_sessions);
}
