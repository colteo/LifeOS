using LifeOS.Application.Gym.History;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Users;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// Workout history queries against real PostgreSQL: the completed-history page (ordering, keyset
// cursor with ties, counts, ownership), the previous-performance lookup (latest completed session
// per exercise, exclusions, duplicate occurrences, bodyweight) and one exercise's history page.
// Every user is new, so the shared database's other rows never interfere.
[Collection(PostgreSqlCollection.Name)]
public class GymHistoryPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task History_IsCompletedOnly_NewestFirst_PagedByKeyset_AndOwnerScoped()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var (bench, row) = await ExercisesAsync(user, "Bench press", "Row");
        var othersBench = (await ExercisesAsync(other, "Bench press", "Row")).First;

        var sessions = new List<WorkoutSession>();
        for (var day = 0; day < 3; day++)
        {
            sessions.Add(await CompletedAsync(user, $"W{day}", Now.AddDays(day), TimeSpan.FromMinutes(45), (bench, [(80m, 8), null]), (row, [(60m, 10)])));
        }

        // Ties on the completion instant are ordered by id.
        sessions.Add(await CompletedAsync(user, "Tie", Now.AddDays(2), TimeSpan.FromMinutes(45), (bench, [(80m, 8)])));
        await InProgressAsync(user, "Current", Now.AddDays(5), (bench, []));
        await CompletedAsync(other, "Other", Now.AddDays(9), TimeSpan.FromMinutes(45), (othersBench, [(90m, 5)]));

        var pages = new List<WorkoutHistoryPage>();
        WorkoutHistoryCursor? cursor = null;
        do
        {
            await using var scope = fixture.CreateScope();
            var page = await new GetWorkoutHistoryHandler(Sessions(scope)).HandleAsync(user.Id, cursor, 3, CancellationToken.None);
            pages.Add(page);
            cursor = page.Next;
        }
        while (cursor is not null);

        Assert.Equal([3, 1], pages.Select(page => page.Items.Count));
        Assert.Equal(
            sessions.OrderByDescending(session => session.CompletedAtUtc).ThenByDescending(session => session.Id).Select(session => session.Id),
            pages.SelectMany(page => page.Items).Select(item => item.Id));

        var w0 = pages.SelectMany(page => page.Items).Single(item => item.WorkoutName == "W0");
        Assert.Equal(("Program", Now, Now.AddMinutes(45)), (w0.ProgramName, w0.StartedAtUtc, w0.CompletedAtUtc));
        // Bench: 3 prescribed, 1 done; row: 3 prescribed, 1 done.
        Assert.Equal((2, 6, 2), (w0.CompletedSetCount, w0.PrescribedSetCount, w0.ExerciseCount));

        await using (var scope = fixture.CreateScope())
        {
            var othersPage = await new GetWorkoutHistoryHandler(Sessions(scope)).HandleAsync(other.Id, null, 50, CancellationToken.None);
            Assert.Equal(["Other"], othersPage.Items.Select(item => item.WorkoutName));
        }
    }

    [Fact]
    public async Task History_SurvivesDeletingTheSourceProgram()
    {
        var user = await NewUserAsync();
        var (bench, _) = await ExercisesAsync(user, "Bench press", "Row");
        var program = WorkoutProgram.Create(user.Id, "Program", Now);
        var workout = program.AddWorkout("Push");
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(bench, 2)]);
        await using (var scope = fixture.CreateScope())
        {
            await Programs(scope).AddAsync(program, CancellationToken.None);
        }

        var session = WorkoutSession.Start(program, workout, Now);
        session.RecordSet(session.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1));
        session.Finish(Now.AddMinutes(30));
        await AddAsync(session);

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Programs(scope).DeleteAsync(user.Id, program.Id, CancellationToken.None));
        }

        await using (var scope = fixture.CreateScope())
        {
            var item = Assert.Single((await Sessions(scope).GetCompletedPageAsync(user.Id, null, 10, CancellationToken.None)));
            Assert.Equal((session.Id, "Program", "Push", 1, 2), (item.Id, item.ProgramName, item.WorkoutName, item.CompletedSetCount, item.PrescribedSetCount));
        }
    }

    [Fact]
    public async Task PreviousPerformance_IsTheLatestCompletedSessionPerExercise_WithEveryRecordedOccurrence()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var (bench, row) = await ExercisesAsync(user, "Bench press", "Row");
        var (pullUp, curl) = await ExercisesAsync(user, "Pull-up", "Curl");
        var othersBench = (await ExercisesAsync(other, "Bench press", "Row")).First;

        await CompletedAsync(user, "Old", Now, TimeSpan.FromMinutes(30), (bench, [(70m, 8)]), (row, [(55m, 10)]), (curl, [(12m, 12)]));
        // Bench twice (blocks 1 and 3), a skipped set, bodyweight pull-ups. Started before "Short" but
        // completed after it: completion time decides.
        var latest = await CompletedAsync(user, "Push", Now.AddDays(2), TimeSpan.FromHours(3),
            (bench, [(100m, 3), null, (100m, 2)]),
            (pullUp, [(null, 12), (null, 10)]),
            (bench, [(70m, 10)]));
        var shortOne = await CompletedAsync(user, "Short", Now.AddDays(2).AddHours(1), TimeSpan.FromMinutes(20), (row, [(60m, 10)]));
        await CompletedAsync(other, "Other", Now.AddDays(3), TimeSpan.FromMinutes(30), (othersBench, [(150m, 1)]));
        var current = await InProgressAsync(user, "Today", Now.AddDays(4), (bench, [(105m, 3)]), (row, []), (pullUp, []));

        PreviousPerformance previous;
        await using (var scope = fixture.CreateScope())
        {
            previous = (await new GetPreviousPerformanceHandler(Sessions(scope)).HandleAsync(user.Id, current.Id, CancellationToken.None))!;
        }

        Assert.Equal([bench.Id, row.Id, pullUp.Id], previous.Exercises.Select(exercise => exercise.ExerciseId));
        var benchPrevious = previous.Exercises[0];
        Assert.Equal((latest.Id, "Push", Now.AddDays(2).AddHours(3)), (benchPrevious.SessionId, benchPrevious.WorkoutName, benchPrevious.CompletedAtUtc));
        Assert.Equal(
            [new PreviousSet(1, 1, 3, 100m), new PreviousSet(1, 3, 2, 100m), new PreviousSet(3, 1, 10, 70m)],
            benchPrevious.Sets);
        Assert.Equal(shortOne.Id, previous.Exercises[1].SessionId);
        Assert.Equal([new PreviousSet(1, 1, 10, 60m)], previous.Exercises[1].Sets);
        Assert.Equal([new PreviousSet(2, 1, 12, null), new PreviousSet(2, 2, 10, null)], previous.Exercises[2].Sets);
    }

    [Fact]
    public async Task PreviousPerformance_ExcludesTheSessionItself_InProgressAndLaterSessions()
    {
        var user = await NewUserAsync();
        var (bench, _) = await ExercisesAsync(user, "Bench press", "Row");
        var earlier = await CompletedAsync(user, "Earlier", Now, TimeSpan.FromMinutes(30), (bench, [(70m, 8)]));
        var viewed = await CompletedAsync(user, "Viewed", Now.AddDays(1), TimeSpan.FromMinutes(30), (bench, [(75m, 8)]));
        await CompletedAsync(user, "Later", Now.AddDays(2), TimeSpan.FromMinutes(30), (bench, [(80m, 8)]));
        await InProgressAsync(user, "Current", Now.AddDays(3), (bench, [(85m, 8)]));

        await using var scope = fixture.CreateScope();
        var sessions = Sessions(scope);

        var forViewed = await sessions.GetPreviousPerformancesAsync(user.Id, [bench.Id], viewed.StartedAtUtc, viewed.Id, CancellationToken.None);
        Assert.Equal(earlier.Id, Assert.Single(forViewed).SessionId);

        var forEarlier = await sessions.GetPreviousPerformancesAsync(user.Id, [bench.Id], earlier.StartedAtUtc, earlier.Id, CancellationToken.None);
        Assert.Empty(forEarlier);

        // A foreign user id sees nothing, whatever the exercise ids.
        var foreign = await sessions.GetPreviousPerformancesAsync(Guid.NewGuid(), [bench.Id], Now.AddYears(1), Guid.Empty, CancellationToken.None);
        Assert.Empty(foreign);

        Assert.Empty(await sessions.GetPreviousPerformancesAsync(user.Id, [], Now.AddYears(1), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ExerciseHistory_IsTheExercisesCompletedSessions_NewestFirst_ByKeyset_OwnerScoped()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var (bench, row) = await ExercisesAsync(user, "Bench press", "Row");
        var othersBench = (await ExercisesAsync(other, "Bench press", "Row")).First;

        var withBench = new List<WorkoutSession>
        {
            await CompletedAsync(user, "W0", Now, TimeSpan.FromMinutes(30), (bench, [(80m, 8), null])),
            // Bench in blocks 1 and 3, bodyweight in the second occurrence.
            await CompletedAsync(user, "W1", Now.AddDays(1), TimeSpan.FromMinutes(30), (bench, [(85m, 5), null, (85m, 4)]), (row, [(60m, 10)]), (bench, [(null, 12)])),
            await CompletedAsync(user, "W2", Now.AddDays(2), TimeSpan.FromMinutes(30), (bench, [(90m, 3)])),
            // Completed at the same instant as W2: ordered by id.
            await CompletedAsync(user, "Tie", Now.AddDays(2), TimeSpan.FromMinutes(30), (bench, [(90m, 2)]))
        };
        await CompletedAsync(user, "Rows only", Now.AddDays(3), TimeSpan.FromMinutes(30), (row, [(60m, 10)]));
        await CompletedAsync(other, "Other", Now.AddDays(3), TimeSpan.FromMinutes(30), (othersBench, [(150m, 1)]));
        var current = await InProgressAsync(user, "Today", Now.AddDays(5), (bench, [(95m, 3)]));

        var pages = new List<IReadOnlyList<ExerciseHistoryEntry>>();
        WorkoutHistoryCursor? cursor = null;
        do
        {
            await using var scope = fixture.CreateScope();
            var page = (await new GetExerciseHistoryHandler(Sessions(scope)).HandleAsync(user.Id, current.Id, bench.Id, cursor, 3, CancellationToken.None))!;
            pages.Add(page.Items);
            cursor = page.Next;
        }
        while (cursor is not null);

        Assert.Equal([3, 1], pages.Select(page => page.Count));
        Assert.Equal(
            withBench.OrderByDescending(session => session.CompletedAtUtc).ThenByDescending(session => session.Id).Select(session => session.Id),
            pages.SelectMany(page => page).Select(entry => entry.SessionId));

        var w1 = pages.SelectMany(page => page).Single(entry => entry.WorkoutName == "W1");
        Assert.Equal(("Program", Now.AddDays(1).AddMinutes(30)), (w1.ProgramName, w1.CompletedAtUtc));
        Assert.Equal([new PreviousSet(1, 1, 5, 85m), new PreviousSet(1, 3, 4, 85m), new PreviousSet(3, 1, 12, null)], w1.Sets);

        await using (var scope = fixture.CreateScope())
        {
            var sessions = Sessions(scope);

            // A foreign user id sees nothing; the session itself and later sessions are excluded.
            Assert.Empty(await sessions.GetExerciseHistoryPageAsync(Guid.NewGuid(), bench.Id, Now.AddYears(1), Guid.Empty, null, 10, CancellationToken.None));
            var beforeW2 = await sessions.GetExerciseHistoryPageAsync(user.Id, bench.Id, withBench[2].StartedAtUtc, withBench[2].Id, null, 10, CancellationToken.None);
            Assert.Equal([withBench[1].Id, withBench[0].Id], beforeW2.Select(entry => entry.SessionId));
        }
    }

    // ---- Helpers ----

    // A workout of Single blocks, one per (exercise, sets); each block prescribes max(values, 3) sets of
    // 8, and a value is (weight, reps) to record or null to leave pending.
    private async Task<WorkoutSession> CompletedAsync(
        User user,
        string workoutName,
        DateTimeOffset startedAt,
        TimeSpan duration,
        params (Exercise Exercise, (decimal? WeightKg, int Reps)?[] Sets)[] blocks)
    {
        var session = await StartedAsync(user, workoutName, startedAt, blocks);
        session.Finish(startedAt + duration);
        await AddAsync(session);

        return session;
    }

    private async Task<WorkoutSession> InProgressAsync(
        User user,
        string workoutName,
        DateTimeOffset startedAt,
        params (Exercise Exercise, (decimal? WeightKg, int Reps)?[] Sets)[] blocks)
    {
        var session = await StartedAsync(user, workoutName, startedAt, blocks);
        await AddAsync(session);

        return session;
    }

    private async Task<WorkoutSession> StartedAsync(
        User user,
        string workoutName,
        DateTimeOffset startedAt,
        (Exercise Exercise, (decimal? WeightKg, int Reps)?[] Sets)[] blocks)
    {
        var program = WorkoutProgram.Create(user.Id, "Program", startedAt);
        var workout = program.AddWorkout(workoutName);

        foreach (var (exercise, sets) in blocks)
        {
            workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(exercise, Math.Max(sets.Length, 3))]);
        }

        await using (var scope = fixture.CreateScope())
        {
            await Programs(scope).AddAsync(program, CancellationToken.None);
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

    private static ExercisePrescription Prescription(Exercise exercise, int sets) =>
        new(exercise, null, Enumerable.Repeat(new RepRange(8, 8), sets).ToList());

    private async Task<(Exercise First, Exercise Second)> ExercisesAsync(User user, string first, string second)
    {
        var exercises = (Exercise.Create(user.Id, first, Now), Exercise.Create(user.Id, second, Now));
        await PostgresAssert.InsertAsync(fixture, exercises.Item1, exercises.Item2);

        return exercises;
    }

    private async Task AddAsync(WorkoutSession session)
    {
        await using var scope = fixture.CreateScope();
        Assert.True(await Sessions(scope).TryAddAsync(session, CancellationToken.None));
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static IWorkoutSessionRepository Sessions(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkoutSessionRepository>();

    private static IWorkoutProgramRepository Programs(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkoutProgramRepository>();
}
