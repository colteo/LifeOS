using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Programs.Blocks;
using LifeOS.Application.Gym.Programs.Workouts;
using LifeOS.Application.Gym.Training;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// Gym authoring against real PostgreSQL: the WorkoutProgram aggregate round-trips in order, edits
// through the real repositories and handlers persist, deletion cascades only through the program's
// own descendants, and the schema backstops (ownership keys, checks, deferred positions, exercise
// name index) hold. Final state is always read from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class GymPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    // ---- Persistence and ordering ----

    [Fact]
    public async Task Program_WithSingleAndSupersetBlocks_RoundTripsInOrder()
    {
        var user = await NewUserAsync();
        var (bench, row, curl) = (Exercise.Create(user.Id, "Bench press", Now), Exercise.Create(user.Id, "Row", Now), Exercise.Create(user.Id, "Curl", Now));
        await PostgresAssert.InsertAsync(fixture, bench, row, curl);

        var program = WorkoutProgram.Create(user.Id, "Hypertrophy", Now);
        var push = program.AddWorkout("Shoulders / Chest / Biceps");
        program.AddWorkout("Back / Triceps / Legs");
        push.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(bench, "Pause at the bottom", (12, 12), (10, 10), (8, 8))]);
        push.AddBlock(WorkoutBlockKind.Superset, 120, [Prescription(row, null, (8, 10), (8, 10), (8, 10)), Prescription(curl, null, (12, 12))]);
        await AddAsync(program);

        var stored = (await LoadAsync(user.Id, program.Id))!;

        Assert.Equal("Hypertrophy", stored.Name);
        Assert.Equal(["Shoulders / Chest / Biceps", "Back / Triceps / Legs"], stored.Workouts.Select(workout => workout.Name));
        Assert.Equal([1, 2], stored.Workouts.Select(workout => workout.Position));

        var blocks = stored.Workouts[0].Blocks;
        Assert.Equal([(1, WorkoutBlockKind.Single, (int?)90), (2, WorkoutBlockKind.Superset, (int?)120)], blocks.Select(block => (block.Position, block.Kind, block.RestSeconds)));

        var single = Assert.Single(blocks[0].Exercises);
        Assert.Equal((bench.Id, "Pause at the bottom"), (single.ExerciseId, single.Notes));
        Assert.Equal([(1, 12, 12), (2, 10, 10), (3, 8, 8)], single.Sets.Select(set => (set.Position, set.TargetMinReps, set.TargetMaxReps)));

        // Superset A/B order persists.
        Assert.Equal([(1, row.Id), (2, curl.Id)], blocks[1].Exercises.Select(exercise => (exercise.Position, exercise.ExerciseId)));
        Assert.Equal([(8, 10), (8, 10), (8, 10)], blocks[1].Exercises[0].Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps)));
    }

    [Fact]
    public async Task Edits_ThroughTheHandlers_PersistNewChangedAndRemovedRows()
    {
        var user = await NewUserAsync();
        var (bench, row, curl) = (Exercise.Create(user.Id, "Bench press", Now), Exercise.Create(user.Id, "Row", Now), Exercise.Create(user.Id, "Curl", Now));
        await PostgresAssert.InsertAsync(fixture, bench, row, curl);
        var program = WorkoutProgram.Create(user.Id, "Program", Now);
        var first = program.AddWorkout("A");
        await AddAsync(program);

        // New workout and new blocks on existing tracked parents.
        var second = (await EditAsync(scope => new AddWorkoutHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, program.Id, "B", CancellationToken.None))).Workouts[1];
        var afterSingle = await EditAsync(scope => new AddWorkoutBlockHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, new AddWorkoutBlockCommand(program.Id, first.Id, WorkoutBlockKind.Single, 60, [Input(bench, 3, 8)]), CancellationToken.None));
        var singleId = afterSingle.Workouts[0].Blocks[0].Id;
        var afterSuperset = await EditAsync(scope => new AddWorkoutBlockHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, new AddWorkoutBlockCommand(program.Id, first.Id, WorkoutBlockKind.Superset, 90, [Input(row, 3, 10), Input(curl, 3, 12)]), CancellationToken.None));
        var supersetId = afterSuperset.Workouts[0].Blocks[1].Id;
        var originalSetIds = afterSingle.Workouts[0].Blocks[0].Exercises[0].Sets.Select(set => set.Id).ToList();

        // Grow the single block's sets from 3 to 4 and change reps; shrink the superset A to 1 set.
        await EditAsync(scope => new UpdateWorkoutBlockHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, new UpdateWorkoutBlockCommand(program.Id, first.Id, singleId, 75,
                [new WorkoutBlockExerciseInput(bench.Id, "Slow", [new(12, 12), new(10, 10), new(8, 8), new(6, 8)])]), CancellationToken.None));
        await EditAsync(scope => new UpdateWorkoutBlockHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, new UpdateWorkoutBlockCommand(program.Id, first.Id, supersetId, 90, [Input(row, 1, 10), Input(curl, 3, 12)]), CancellationToken.None));

        // Swap the workouts and the blocks: positions are exchanged within one save.
        await EditAsync(scope => new ReorderWorkoutsHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, program.Id, [second.Id, first.Id], CancellationToken.None));
        await EditAsync(scope => new ReorderWorkoutBlocksHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, program.Id, first.Id, [supersetId, singleId], CancellationToken.None));

        var stored = (await LoadAsync(user.Id, program.Id))!;
        Assert.Equal([("B", 1), ("A", 2)], stored.Workouts.Select(workout => (workout.Name, workout.Position)));
        var blocks = stored.Workouts[1].Blocks;
        Assert.Equal([supersetId, singleId], blocks.Select(block => block.Id));
        Assert.Equal([1, 2], blocks.Select(block => block.Position));

        var single = blocks[1];
        Assert.Equal(75, single.RestSeconds);
        Assert.Equal("Slow", single.Exercises[0].Notes);
        Assert.Equal([(12, 12), (10, 10), (8, 8), (6, 8)], single.Exercises[0].Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps)));
        // Existing set rows keep their identity; only the 4th is new.
        Assert.Equal(originalSetIds, single.Exercises[0].Sets.Take(3).Select(set => set.Id));

        Assert.Equal([1], blocks[0].Exercises[0].Sets.Select(set => set.Position));
        Assert.Equal(3, blocks[0].Exercises[1].Sets.Count);

        // Delete the first block: the remaining one closes the gap.
        await EditAsync(scope => new DeleteWorkoutBlockHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, program.Id, first.Id, supersetId, CancellationToken.None));

        var afterDelete = (await LoadAsync(user.Id, program.Id))!;
        var remaining = Assert.Single(afterDelete.Workouts[1].Blocks);
        Assert.Equal((singleId, 1), (remaining.Id, remaining.Position));
        Assert.Equal(0, await CountAsync($"SELECT count(*)::int AS \"Value\" FROM workout_block_exercises WHERE workout_block_id = {supersetId}"));
    }

    // ---- Training choices ----

    [Fact]
    public async Task TrainingPrograms_CountBlocksExercisesAndSets_ForTheOwnerOnly()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var (bench, row) = (Exercise.Create(user.Id, "Bench press", Now), Exercise.Create(user.Id, "Row", Now));
        var (othersBench, othersRow) = (Exercise.Create(other.Id, "Bench press", Now), Exercise.Create(other.Id, "Row", Now));
        await PostgresAssert.InsertAsync(fixture, bench, row, othersBench, othersRow);

        // "Day 1": a Single of 2 sets and a Superset of 1 + 1 sets.
        var program = ProgramWithBlocks(user.Id, bench, row);
        program.AddWorkout("Day 2");
        await AddAsync(program);
        await AddAsync(WorkoutProgram.Create(user.Id, "Empty", Now));
        await AddAsync(ProgramWithBlocks(other.Id, othersBench, othersRow));

        await using var scope = fixture.CreateScope();
        var training = await new GetTrainingProgramsHandler(Programs(scope)).HandleAsync(user.Id, CancellationToken.None);

        Assert.Equal([("Empty", 0), ("Program", 2)], training.Select(item => (item.Name, item.Workouts.Count)));
        Assert.Equal(program.Id, training[1].Id);
        Assert.Equal(
            [("Day 1", 1, 2, 3, 4, true), ("Day 2", 2, 0, 0, 0, false)],
            training[1].Workouts.Select(workout => (workout.Name, workout.Position, workout.BlockCount, workout.ExerciseCount, workout.PrescribedSetCount, workout.CanStart)));
    }

    // ---- Deletion ----

    [Fact]
    public async Task DeleteProgram_RemovesItsDescendants_KeepsExercisesAndOtherPrograms()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var (bench, row) = (Exercise.Create(user.Id, "Bench press", Now), Exercise.Create(user.Id, "Row", Now));
        var othersBench = Exercise.Create(other.Id, "Bench press", Now);
        await PostgresAssert.InsertAsync(fixture, bench, row, othersBench);

        var doomed = ProgramWithBlocks(user.Id, bench, row);
        var kept = ProgramWithBlocks(user.Id, bench, row);
        var othersProgram = WorkoutProgram.Create(other.Id, "Other", Now);
        othersProgram.AddWorkout("Day").AddBlock(WorkoutBlockKind.Single, 60, [Prescription(othersBench, null, (5, 5))]);
        await AddAsync(doomed);
        await AddAsync(kept);
        await AddAsync(othersProgram);

        await using (var scope = fixture.CreateScope())
        {
            // Another user's delete is a no-op.
            Assert.False(await Programs(scope).DeleteAsync(other.Id, doomed.Id, CancellationToken.None));
            Assert.True(await Programs(scope).DeleteAsync(user.Id, doomed.Id, CancellationToken.None));
            Assert.False(await Programs(scope).DeleteAsync(user.Id, doomed.Id, CancellationToken.None));
        }

        Assert.Null(await LoadAsync(user.Id, doomed.Id));
        Assert.Equal(0, await CountAsync($"SELECT count(*)::int AS \"Value\" FROM workout_templates WHERE workout_program_id = {doomed.Id}"));

        var doomedBlockIds = doomed.Workouts.SelectMany(workout => workout.Blocks).Select(block => block.Id).ToArray();
        var doomedExerciseRowIds = doomed.Workouts.SelectMany(workout => workout.Blocks).SelectMany(block => block.Exercises).Select(exercise => exercise.Id).ToArray();
        Assert.Equal(0, await CountAsync($"SELECT count(*)::int AS \"Value\" FROM workout_blocks WHERE id = ANY({doomedBlockIds})"));
        Assert.Equal(0, await CountAsync($"SELECT count(*)::int AS \"Value\" FROM workout_block_exercises WHERE id = ANY({doomedExerciseRowIds})"));
        Assert.Equal(0, await CountAsync($"SELECT count(*)::int AS \"Value\" FROM workout_set_prescriptions WHERE workout_block_exercise_id = ANY({doomedExerciseRowIds})"));

        // Exercises (reused by the kept program) and everything else remain.
        await using var verify = fixture.CreateScope();
        Assert.Equal(2, (await Exercises(verify).GetAllAsync(user.Id, CancellationToken.None)).Count);
        Assert.Single(await Exercises(verify).GetAllAsync(other.Id, CancellationToken.None));
        Assert.Equal(2, (await LoadAsync(user.Id, kept.Id))!.Workouts[0].Blocks.Count);
        Assert.NotNull(await LoadAsync(other.Id, othersProgram.Id));
    }

    [Fact]
    public async Task Exercise_UsedByAProgram_CannotBeDeleted()
    {
        var user = await NewUserAsync();
        var (bench, row) = (Exercise.Create(user.Id, "Bench press", Now), Exercise.Create(user.Id, "Row", Now));
        await PostgresAssert.InsertAsync(fixture, bench, row);
        await AddAsync(ProgramWithBlocks(user.Id, bench, row));

        await PostgresAssert.DeleteBlockedAsync(
            "FK_workout_block_exercises_exercises",
            () => ExecuteAsync($"DELETE FROM exercises WHERE id = {bench.Id}"));
    }

    // ---- Exercises ----

    [Fact]
    public async Task ExerciseNames_AreUniquePerUserIgnoringCase_ButNotAcrossUsers()
    {
        var (a, b) = (await NewUserAsync(), await NewUserAsync());

        await using var scope = fixture.CreateScope();
        var exercises = Exercises(scope);

        Assert.True(await exercises.TryAddAsync(Exercise.Create(a.Id, "Bench press", Now), CancellationToken.None));
        Assert.False(await exercises.TryAddAsync(Exercise.Create(a.Id, "BENCH PRESS", Now), CancellationToken.None));
        Assert.True(await exercises.TryAddAsync(Exercise.Create(b.Id, "Bench press", Now), CancellationToken.None));
        // The failed add was detached: a later save in the same scope does not retry it.
        Assert.True(await exercises.TryAddAsync(Exercise.Create(a.Id, "Squat", Now), CancellationToken.None));

        await using var verify = fixture.CreateScope();
        Assert.Equal(["Bench press", "Squat"], (await Exercises(verify).GetAllAsync(a.Id, CancellationToken.None)).Select(e => e.Name).Order());
        Assert.Single(await Exercises(verify).GetAllAsync(b.Id, CancellationToken.None));
    }

    // ---- Ownership ----

    [Fact]
    public async Task Repositories_NeverReturnOrChangeAnotherUsersProgram()
    {
        var (a, b) = (await NewUserAsync(), await NewUserAsync());
        var (bench, row) = (Exercise.Create(a.Id, "Bench press", Now), Exercise.Create(a.Id, "Row", Now));
        await PostgresAssert.InsertAsync(fixture, bench, row);
        var program = ProgramWithBlocks(a.Id, bench, row);
        await AddAsync(program);

        await using var scope = fixture.CreateScope();
        Assert.Null(await Programs(scope).GetAsync(b.Id, program.Id, CancellationToken.None));
        Assert.Null(await Programs(scope).GetForUpdateAsync(b.Id, program.Id, CancellationToken.None));
        Assert.Empty(await Programs(scope).GetSummariesAsync(b.Id, CancellationToken.None));
        Assert.Empty(await Exercises(scope).GetByIdsAsync(b.Id, [bench.Id], CancellationToken.None));

        var summary = Assert.Single(await Programs(scope).GetSummariesAsync(a.Id, CancellationToken.None));
        Assert.Equal((program.Id, 1), (summary.Id, summary.WorkoutCount));
    }

    [Fact]
    public async Task Schema_RejectsABlockExerciseReferencingAnotherUsersExercise()
    {
        var (a, b) = (await NewUserAsync(), await NewUserAsync());
        var (bench, row) = (Exercise.Create(a.Id, "Bench press", Now), Exercise.Create(a.Id, "Row", Now));
        var othersExercise = Exercise.Create(b.Id, "Squat", Now);
        await PostgresAssert.InsertAsync(fixture, bench, row, othersExercise);
        var program = ProgramWithBlocks(a.Id, bench, row);
        await AddAsync(program);
        var rowId = program.Workouts[0].Blocks[0].Exercises[0].Id;

        await PostgresAssert.ViolatesAsync(
            PostgresAssert.ForeignKeyViolation,
            "FK_workout_block_exercises_exercises",
            () => ExecuteAsync($"UPDATE workout_block_exercises SET exercise_id = {othersExercise.Id} WHERE id = {rowId}"));
    }

    // ---- Schema backstops ----

    [Fact]
    public async Task Schema_RejectsInvalidRepsAndRest()
    {
        var user = await NewUserAsync();
        var (bench, row) = (Exercise.Create(user.Id, "Bench press", Now), Exercise.Create(user.Id, "Row", Now));
        await PostgresAssert.InsertAsync(fixture, bench, row);
        var program = ProgramWithBlocks(user.Id, bench, row);
        await AddAsync(program);
        var block = program.Workouts[0].Blocks[0];
        var set = block.Exercises[0].Sets[0];

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_workout_set_prescriptions_reps",
            () => ExecuteAsync($"UPDATE workout_set_prescriptions SET target_min_reps = 0 WHERE id = {set.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_workout_set_prescriptions_reps",
            () => ExecuteAsync($"UPDATE workout_set_prescriptions SET target_min_reps = 10, target_max_reps = 8 WHERE id = {set.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_workout_blocks_rest_seconds",
            () => ExecuteAsync($"UPDATE workout_blocks SET rest_seconds = 0 WHERE id = {block.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_workout_blocks_kind",
            () => ExecuteAsync($"UPDATE workout_blocks SET kind = 'Circuit' WHERE id = {block.Id}"));
    }

    [Fact]
    public async Task Schema_RejectsDuplicateSiblingPositionsAtCommit()
    {
        var user = await NewUserAsync();
        var program = WorkoutProgram.Create(user.Id, "Program", Now);
        program.AddWorkout("A");
        var second = program.AddWorkout("B");
        await AddAsync(program);

        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        await using var transaction = await database.BeginTransactionAsync();

        // Deferred: the statement itself succeeds; the commit is rejected.
        await database.ExecuteSqlAsync($"UPDATE workout_templates SET position = 1 WHERE id = {second.Id}");

        await PostgresAssert.ViolatesAsync(
            PostgresAssert.UniqueViolation,
            "ux_workout_templates_program_position",
            () => transaction.CommitAsync());
    }

    // ---- Concurrency ----

    [Fact]
    public async Task ConcurrentEdits_OfOneProgram_RunOneAfterAnother()
    {
        var user = await NewUserAsync();
        var program = WorkoutProgram.Create(user.Id, "Program", Now);
        await AddAsync(program);

        // Blocker: holds the program for update and appends a workout, not yet saved.
        await using var blockerScope = fixture.CreateScope();
        var blockerPrograms = Programs(blockerScope);
        var held = (await blockerPrograms.GetForUpdateAsync(user.Id, program.Id, CancellationToken.None))!;
        held.AddWorkout("First");

        await using var scope = fixture.CreateScope();
        var add = new AddWorkoutHandler(Programs(scope), Exercises(scope))
            .HandleAsync(user.Id, program.Id, "Second", CancellationToken.None);

        // The handler waits for the program's lock, then sees the committed first workout.
        await WaitUntilASessionWaitsForALockAsync();
        await blockerPrograms.SaveAsync(held, CancellationToken.None);

        Assert.Equal(WorkoutProgramEditStatus.Updated, (await add).Status);
        var stored = (await LoadAsync(user.Id, program.Id))!;
        Assert.Equal([("First", 1), ("Second", 2)], stored.Workouts.Select(workout => (workout.Name, workout.Position)));
    }

    // ---- Helpers ----

    private static WorkoutProgram ProgramWithBlocks(Guid userId, Exercise a, Exercise b)
    {
        var program = WorkoutProgram.Create(userId, "Program", Now);
        var workout = program.AddWorkout("Day 1");
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(a, null, (8, 8), (8, 8))]);
        workout.AddBlock(WorkoutBlockKind.Superset, 120, [Prescription(a, null, (10, 10)), Prescription(b, "B", (12, 12))]);

        return program;
    }

    private static ExercisePrescription Prescription(Exercise exercise, string? notes, params (int Min, int Max)[] sets) =>
        new(exercise, notes, sets.Select(set => new RepRange(set.Min, set.Max)).ToList());

    private static WorkoutBlockExerciseInput Input(Exercise exercise, int sets, int reps) =>
        new(exercise.Id, null, Enumerable.Repeat(new SetTargetInput(reps, reps), sets).ToList());

    private async Task AddAsync(WorkoutProgram program)
    {
        await using var scope = fixture.CreateScope();
        await Programs(scope).AddAsync(program, CancellationToken.None);
    }

    private async Task<WorkoutProgramDetails> EditAsync(Func<AsyncServiceScope, Task<WorkoutProgramEditResult>> edit)
    {
        await using var scope = fixture.CreateScope();
        var result = await edit(scope);

        Assert.Equal(WorkoutProgramEditStatus.Updated, result.Status);

        return result.Program!;
    }

    private async Task<WorkoutProgram?> LoadAsync(Guid userId, Guid programId)
    {
        await using var scope = fixture.CreateScope();

        return await Programs(scope).GetAsync(userId, programId, CancellationToken.None);
    }

    private async Task<int> CountAsync(FormattableString sql)
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Database.SqlQuery<int>(sql).SingleAsync();
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

    private static IWorkoutProgramRepository Programs(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkoutProgramRepository>();

    private static IExerciseRepository Exercises(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IExerciseRepository>();
}
