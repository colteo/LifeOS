using LifeOS.Application.Gym.Training;
using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Programs.Blocks;
using LifeOS.Application.Gym.Programs.Workouts;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// Workout execution against real PostgreSQL: the WorkoutSession snapshot round-trips in order with
// its execution state, survives edits and deletion of its source program, at most one InProgress
// session exists per user, and the schema backstops (ownership keys, checks, weight precision) hold.
// Final state is always read from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class GymSessionPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    // ---- Round trip ----

    [Fact]
    public async Task Session_RoundTripsInOrder_WithExecutionState()
    {
        var (user, program, workout) = await ProgramAsync();
        var session = WorkoutSession.Start(program, workout, Now);
        await AddAsync(session);

        var single = session.Blocks[0].Exercises[0].Sets;
        var superset = session.Blocks[1].Exercises;
        await ChangeAsync(user.Id, session.Id, stored => stored.RecordSet(single[0].Id, 8, 82.5m, Now.AddMinutes(2)));
        await ChangeAsync(user.Id, session.Id, stored => stored.RecordSet(superset[1].Sets[0].Id, 12, null, Now.AddMinutes(9)));
        await ChangeAsync(user.Id, session.Id, stored => stored.Finish(Now.AddMinutes(40)));

        var loaded = (await LoadAsync(user.Id, session.Id))!;

        Assert.Equal((program.Id, workout.Id, "Program", "Push"), (loaded.WorkoutProgramId!.Value, loaded.WorkoutTemplateId!.Value, loaded.ProgramName, loaded.WorkoutName));
        Assert.Equal((WorkoutSessionStatus.Completed, Now, (DateTimeOffset?)Now.AddMinutes(40)), (loaded.Status, loaded.StartedAtUtc, loaded.CompletedAtUtc));
        Assert.Equal([(1, WorkoutBlockKind.Single, (int?)90), (2, WorkoutBlockKind.Superset, (int?)120)], loaded.Blocks.Select(block => (block.Position, block.Kind, block.RestSeconds)));
        Assert.Equal("Pause", loaded.Blocks[0].Exercises[0].Notes);
        Assert.Equal([(1, 8, 8), (2, 8, 8), (3, 8, 8)], loaded.Blocks[0].Exercises[0].Sets.Select(set => (set.Position, set.TargetMinReps, set.TargetMaxReps)));
        Assert.Equal([1, 2], loaded.Blocks[1].Exercises.Select(exercise => exercise.Position));
        Assert.Equal(superset.Select(exercise => exercise.ExerciseId), loaded.Blocks[1].Exercises.Select(exercise => exercise.ExerciseId));

        var first = loaded.Blocks[0].Exercises[0].Sets[0];
        Assert.Equal((8, (decimal?)82.5m, (DateTimeOffset?)Now.AddMinutes(2)), (first.ActualReps, first.WeightKg, first.CompletedAtUtc));
        var curl = loaded.Blocks[1].Exercises[1].Sets[0];
        Assert.Equal((12, (decimal?)null), (curl.ActualReps, curl.WeightKg));
        Assert.Equal((2, 6), (loaded.CompletedSetCount, loaded.PrescribedSetCount));
        Assert.Equal("82.50", await ScalarAsync<string>($"SELECT weight_kg::text AS \"Value\" FROM workout_session_sets WHERE id = {first.Id}"));
    }

    // ---- Snapshot vs. authoring ----

    [Fact]
    public async Task Snapshot_SurvivesEditingAndDeletingTheSourceProgram()
    {
        var (user, program, workout) = await ProgramAsync();
        var session = WorkoutSession.Start(program, workout, Now);
        await AddAsync(session);
        await ChangeAsync(user.Id, session.Id, stored => stored.RecordSet(stored.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1)));
        await ChangeAsync(user.Id, session.Id, stored => stored.Finish(Now.AddMinutes(30)));
        var singleBlock = workout.Blocks[0];
        var bench = singleBlock.Exercises[0].ExerciseId;

        // The template becomes 4 × 10 with 60 s rest, the workout is deleted, then the program.
        await using (var scope = fixture.CreateScope())
        {
            var result = await new UpdateWorkoutBlockHandler(Programs(scope), Exercises(scope)).HandleAsync(
                user.Id,
                new UpdateWorkoutBlockCommand(program.Id, workout.Id, singleBlock.Id, 60, [new WorkoutBlockExerciseInput(bench, null, Enumerable.Repeat(new SetTargetInput(10, 10), 4).ToList())]),
                CancellationToken.None);
            Assert.Equal(WorkoutProgramEditStatus.Updated, result.Status);
        }

        await using (var scope = fixture.CreateScope())
        {
            var result = await new DeleteWorkoutHandler(Programs(scope), Exercises(scope), Progress(scope)).HandleAsync(user.Id, program.Id, workout.Id, CancellationToken.None);
            Assert.Equal(WorkoutProgramEditStatus.Updated, result.Status);
        }

        var afterWorkoutDeleted = (await LoadAsync(user.Id, session.Id))!;
        Assert.Equal(((Guid?)program.Id, (Guid?)null), (afterWorkoutDeleted.WorkoutProgramId, afterWorkoutDeleted.WorkoutTemplateId));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Programs(scope).DeleteAsync(user.Id, program.Id, CancellationToken.None));
        }

        var loaded = (await LoadAsync(user.Id, session.Id))!;
        Assert.Equal(((Guid?)null, (Guid?)null), (loaded.WorkoutProgramId, loaded.WorkoutTemplateId));
        Assert.Equal(("Program", "Push", WorkoutSessionStatus.Completed), (loaded.ProgramName, loaded.WorkoutName, loaded.Status));
        Assert.Equal((int?)90, loaded.Blocks[0].RestSeconds);
        Assert.Equal([(8, 8), (8, 8), (8, 8)], loaded.Blocks[0].Exercises[0].Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps)));
        Assert.Equal((8, (decimal?)80m), (loaded.ExecutionOrder[0].ActualReps, loaded.ExecutionOrder[0].WeightKg));
        Assert.Equal(6, loaded.PrescribedSetCount);
    }

    [Fact]
    public async Task Exercise_WithExecutionHistory_CannotBeDeleted()
    {
        var (user, program, workout) = await ProgramAsync();
        var session = WorkoutSession.Start(program, workout, Now);
        await AddAsync(session);
        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Programs(scope).DeleteAsync(user.Id, program.Id, CancellationToken.None));
        }

        await PostgresAssert.DeleteBlockedAsync(
            "FK_workout_session_exercises_exercises",
            () => ExecuteAsync($"DELETE FROM exercises WHERE id = {session.Blocks[0].Exercises[0].ExerciseId}"));
    }

    // ---- One InProgress per user ----

    [Fact]
    public async Task AtMostOneInProgressSessionPerUser()
    {
        var (user, program, workout) = await ProgramAsync();
        var (_, othersProgram, othersWorkout) = await ProgramAsync();
        var first = WorkoutSession.Start(program, workout, Now);
        await AddAsync(first);

        await using (var scope = fixture.CreateScope())
        {
            var sessions = Sessions(scope);
            Assert.False(await sessions.TryAddAsync(WorkoutSession.Start(program, workout, Now.AddMinutes(1)), CancellationToken.None));
            Assert.True(await sessions.TryAddAsync(WorkoutSession.Start(othersProgram, othersWorkout, Now), CancellationToken.None));
        }

        // Finished: the next one may start.
        await ChangeAsync(user.Id, first.Id, stored => stored.Finish(Now.AddMinutes(30)));
        var second = WorkoutSession.Start(program, workout, Now.AddDays(1));
        await AddAsync(second);

        await using var verify = fixture.CreateScope();
        Assert.Equal(second.Id, (await Sessions(verify).GetInProgressAsync(user.Id, CancellationToken.None))!.Id);
        Assert.Equal(2, await ScalarAsync<int>($"SELECT count(*)::int AS \"Value\" FROM workout_sessions WHERE user_id = {user.Id}"));

        // The index itself rejects a second InProgress row.
        await PostgresAssert.ViolatesAsync(
            PostgresAssert.UniqueViolation,
            "ux_workout_sessions_user_in_progress",
            () => ExecuteAsync($"UPDATE workout_sessions SET status = 'InProgress', completed_at_utc = NULL WHERE id = {first.Id}"));
    }

    [Fact]
    public async Task ConcurrentStarts_OnlyOneWins()
    {
        var (user, program, workout) = await ProgramAsync();

        await using var scopeA = fixture.CreateScope();
        await using var scopeB = fixture.CreateScope();
        var results = await Task.WhenAll(
            Sessions(scopeA).TryAddAsync(WorkoutSession.Start(program, workout, Now), CancellationToken.None),
            Sessions(scopeB).TryAddAsync(WorkoutSession.Start(program, workout, Now), CancellationToken.None));

        Assert.Equal(1, results.Count(added => added));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT count(*)::int AS \"Value\" FROM workout_sessions WHERE user_id = {user.Id}"));
    }

    [Fact]
    public async Task StartHandler_LosingTheIndexRace_ReportsTheInProgressSession()
    {
        var (user, program, workout) = await ProgramAsync();
        var existing = WorkoutSession.Start(program, workout, Now);
        await AddAsync(existing);

        await using var scope = fixture.CreateScope();
        var result = await new StartWorkoutSessionHandler(Programs(scope), Sessions(scope), Exercises(scope), TimeProvider.System)
            .HandleAsync(user.Id, program.Id, workout.Id, CancellationToken.None);

        Assert.Equal((StartWorkoutSessionStatus.AnotherInProgress, (Guid?)existing.Id), (result.Status, result.InProgressSessionId));
    }

    // ---- Discard ----

    [Fact]
    public async Task Discard_RemovesTheSnapshot_AndKeepsExercisesAndTheProgram()
    {
        var (user, program, workout) = await ProgramAsync();
        var session = WorkoutSession.Start(program, workout, Now);
        await AddAsync(session);
        await ChangeAsync(user.Id, session.Id, stored => stored.RecordSet(stored.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1)));

        await using (var scope = fixture.CreateScope())
        {
            var status = await new DiscardWorkoutSessionHandler(Sessions(scope)).HandleAsync(user.Id, session.Id, CancellationToken.None);
            Assert.Equal(WorkoutSessionChangeStatus.Changed, status);
        }

        var blockIds = session.Blocks.Select(block => block.Id).ToArray();
        Assert.Null(await LoadAsync(user.Id, session.Id));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT count(*)::int AS \"Value\" FROM workout_session_blocks WHERE id = ANY({blockIds})"));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT count(*)::int AS \"Value\" FROM workout_session_exercises WHERE workout_session_block_id = ANY({blockIds})"));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT count(*)::int AS \"Value\" FROM workout_session_sets WHERE id = {session.ExecutionOrder[0].Id}"));

        await using var verify = fixture.CreateScope();
        Assert.Equal(3, (await Exercises(verify).GetAllAsync(user.Id, CancellationToken.None)).Count);
        Assert.NotNull(await Programs(verify).GetAsync(user.Id, program.Id, CancellationToken.None));
    }

    // ---- Ownership ----

    [Fact]
    public async Task Repositories_NeverReturnAnotherUsersSession()
    {
        var (user, program, workout) = await ProgramAsync();
        var other = await NewUserAsync();
        var session = WorkoutSession.Start(program, workout, Now);
        await AddAsync(session);

        await using var scope = fixture.CreateScope();
        Assert.Null(await Sessions(scope).GetAsync(other.Id, session.Id, CancellationToken.None));
        Assert.Null(await Sessions(scope).GetForUpdateAsync(other.Id, session.Id, CancellationToken.None));
        Assert.Null(await Sessions(scope).GetInProgressAsync(other.Id, CancellationToken.None));
        Assert.NotNull(await Sessions(scope).GetInProgressAsync(user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Schema_RejectsSnapshotRowsOfAnotherUser()
    {
        var (_, program, workout) = await ProgramAsync();
        var other = await NewUserAsync();
        var othersExercise = Exercise.Create(other.Id, "Squat", Now);
        await PostgresAssert.InsertAsync(fixture, othersExercise);
        var session = WorkoutSession.Start(program, workout, Now);
        await AddAsync(session);
        var exerciseRowId = session.Blocks[0].Exercises[0].Id;

        await PostgresAssert.ViolatesAsync(
            PostgresAssert.ForeignKeyViolation,
            "FK_workout_session_exercises_exercises",
            () => ExecuteAsync($"UPDATE workout_session_exercises SET exercise_id = {othersExercise.Id} WHERE id = {exerciseRowId}"));
        await PostgresAssert.ViolatesOneOfAsync(
            PostgresAssert.ForeignKeyViolation,
            ["FK_workout_session_blocks_workout_sessions", "FK_workout_session_exercises_workout_session_blocks"],
            () => ExecuteAsync($"UPDATE workout_session_blocks SET user_id = {other.Id} WHERE id = {session.Blocks[0].Id}"));
    }

    // ---- Schema backstops ----

    [Fact]
    public async Task Schema_RejectsInvalidExecutionState()
    {
        var (_, program, workout) = await ProgramAsync();
        var session = WorkoutSession.Start(program, workout, Now);
        await AddAsync(session);
        var setId = session.ExecutionOrder[0].Id;

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_workout_session_sets_actual",
            () => ExecuteAsync($"UPDATE workout_session_sets SET actual_reps = 0, completed_at_utc = now() WHERE id = {setId}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_workout_session_sets_actual",
            () => ExecuteAsync($"UPDATE workout_session_sets SET actual_reps = 8, weight_kg = 0, completed_at_utc = now() WHERE id = {setId}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_workout_session_sets_actual",
            () => ExecuteAsync($"UPDATE workout_session_sets SET actual_reps = 8 WHERE id = {setId}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_workout_sessions_completion",
            () => ExecuteAsync($"UPDATE workout_sessions SET status = 'Completed' WHERE id = {session.Id}"));
        // An unknown status also fails the completion check; PostgreSQL does not define which fires first.
        await PostgresAssert.ViolatesOneOfAsync(PostgresErrorCodes.CheckViolation, ["ck_workout_sessions_status", "ck_workout_sessions_completion"],
            () => ExecuteAsync($"UPDATE workout_sessions SET status = 'Paused' WHERE id = {session.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "ux_workout_session_sets_exercise_position",
            () => ExecuteAsync($"UPDATE workout_session_sets SET position = 2 WHERE id = {setId}"));
    }

    [Fact]
    public async Task Schema_WeightIsNumeric_AndInProgressIndexIsPartial()
    {
        Assert.Equal("numeric(6,2)", await ScalarAsync<string>(
            $"""
            SELECT data_type || '(' || numeric_precision || ',' || numeric_scale || ')' AS "Value"
            FROM information_schema.columns
            WHERE table_name = 'workout_session_sets' AND column_name = 'weight_kg'
            """));

        var index = await ScalarAsync<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_workout_sessions_user_in_progress'");
        Assert.Contains("UNIQUE", index);
        Assert.Contains("WHERE", index);
        Assert.Contains("InProgress", index);

        // Source references never delete execution history.
        var sourceDeleteRules = await StringsAsync(
            $"""
            SELECT rc.constraint_name || ' ' || rc.delete_rule AS "Value"
            FROM information_schema.referential_constraints rc
            WHERE rc.constraint_name IN ('FK_workout_sessions_workout_programs', 'FK_workout_sessions_workout_templates')
            ORDER BY 1
            """);
        Assert.Equal(["FK_workout_sessions_workout_programs SET NULL", "FK_workout_sessions_workout_templates SET NULL"], sourceDeleteRules);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task ConcurrentChanges_OfOneSession_RunOneAfterAnother()
    {
        var (user, program, workout) = await ProgramAsync();
        var session = WorkoutSession.Start(program, workout, Now);
        await AddAsync(session);

        // Blocker: holds the session for update and records set 1, not yet saved.
        await using var blockerScope = fixture.CreateScope();
        var blockerSessions = Sessions(blockerScope);
        var held = (await blockerSessions.GetForUpdateAsync(user.Id, session.Id, CancellationToken.None))!;
        held.RecordSet(held.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1));

        await using var scope = fixture.CreateScope();
        var finish = new FinishWorkoutSessionHandler(Sessions(scope), Exercises(scope), Progress(scope), TimeProvider.System)
            .HandleAsync(user.Id, session.Id, CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blockerSessions.SaveAsync(held, CancellationToken.None);

        Assert.Equal(WorkoutSessionChangeStatus.Changed, (await finish).Status);
        var stored = (await LoadAsync(user.Id, session.Id))!;
        Assert.Equal((WorkoutSessionStatus.Completed, 1), (stored.Status, stored.CompletedSetCount));
    }

    // ---- Helpers ----

    // A user with a program whose "Push" workout has a Single bench block (3 × 8, rest 90, "Pause")
    // and a Superset of row and curl (2 + 1 sets, rest 120).
    private async Task<(User User, WorkoutProgram Program, WorkoutTemplate Workout)> ProgramAsync()
    {
        var user = await NewUserAsync();
        var (bench, row, curl) = (Exercise.Create(user.Id, "Bench press", Now), Exercise.Create(user.Id, "Row", Now), Exercise.Create(user.Id, "Curl", Now));
        await PostgresAssert.InsertAsync(fixture, bench, row, curl);

        var program = WorkoutProgram.Create(user.Id, "Program", Now);
        var workout = program.AddWorkout("Push");
        workout.AddBlock(WorkoutBlockKind.Single, 90, [new ExercisePrescription(bench, "Pause", [new(8, 8), new(8, 8), new(8, 8)])]);
        workout.AddBlock(WorkoutBlockKind.Superset, 120, [new ExercisePrescription(row, null, [new(10, 10), new(10, 10)]), new ExercisePrescription(curl, null, [new(12, 12)])]);

        await using var scope = fixture.CreateScope();
        await Programs(scope).AddAsync(program, CancellationToken.None);

        return (user, program, workout);
    }

    private async Task AddAsync(WorkoutSession session)
    {
        await using var scope = fixture.CreateScope();
        Assert.True(await Sessions(scope).TryAddAsync(session, CancellationToken.None));
    }

    private async Task ChangeAsync(Guid userId, Guid sessionId, Action<WorkoutSession> change)
    {
        await using var scope = fixture.CreateScope();
        var sessions = Sessions(scope);
        var session = (await sessions.GetForUpdateAsync(userId, sessionId, CancellationToken.None))!;
        change(session);
        await sessions.SaveAsync(session, CancellationToken.None);
    }

    private async Task<WorkoutSession?> LoadAsync(Guid userId, Guid sessionId)
    {
        await using var scope = fixture.CreateScope();

        return await Sessions(scope).GetAsync(userId, sessionId, CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(FormattableString sql)
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Database.SqlQuery<T>(sql).SingleAsync();
    }

    private async Task<List<string>> StringsAsync(FormattableString sql)
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Database.SqlQuery<string>(sql).ToListAsync();
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlAsync(sql);
    }

    private async Task WaitUntilASessionWaitsForALockAsync()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = Db(scope);
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var waiting = await dbContext.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE NOT granted")
                .SingleAsync();

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The operation never waited for the blocker's lock.");
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static IWorkoutSessionRepository Sessions(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkoutSessionRepository>();

    private static IWorkoutProgramRepository Programs(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkoutProgramRepository>();

    private static IExerciseRepository Exercises(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IExerciseRepository>();

    private static ActiveProgramProgress Progress(AsyncServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<IActiveProgramRepository>(), Programs(scope), TimeProvider.System);
}
