using LifeOS.Application.Gym.Training;
using LifeOS.Application.Gym.Exercises.CreateExercise;
using LifeOS.Application.Gym.Exercises.GetExercises;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Programs.Blocks;
using LifeOS.Application.Gym.Programs.CreateWorkoutProgram;
using LifeOS.Application.Gym.Programs.DeleteWorkoutProgram;
using LifeOS.Application.Gym.Programs.GetWorkoutProgram;
using LifeOS.Application.Gym.Programs.GetWorkoutPrograms;
using LifeOS.Application.Gym.Programs.RenameWorkoutProgram;
using LifeOS.Application.Gym.Programs.Workouts;
using LifeOS.Domain.Gym.Programs;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Gym;

public class GymHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryExerciseRepository _exercises = new();
    private readonly InMemoryWorkoutProgramRepository _programs = new();

    // ---- Exercises ----

    [Fact]
    public async Task CreateExercise_StoresIt_AndRejectsADuplicateNameIgnoringCase()
    {
        var handler = new CreateExerciseHandler(_exercises, new FixedTimeProvider(Now));

        var created = await handler.HandleAsync(TestUsers.A, " Bench press ", CancellationToken.None);
        var duplicate = await handler.HandleAsync(TestUsers.A, "BENCH PRESS", CancellationToken.None);

        Assert.Equal(CreateExerciseStatus.Created, created.Status);
        Assert.Equal(("Bench press", Now), (created.Exercise!.Name, created.Exercise.CreatedAtUtc));
        Assert.Equal(CreateExerciseStatus.DuplicateName, duplicate.Status);
        Assert.Single(_exercises.Exercises);
    }

    [Fact]
    public async Task CreateExercise_SameNameForAnotherUser_IsAllowed()
    {
        var handler = new CreateExerciseHandler(_exercises, new FixedTimeProvider(Now));

        Assert.Equal(CreateExerciseStatus.Created, (await handler.HandleAsync(TestUsers.A, "Squat", CancellationToken.None)).Status);
        Assert.Equal(CreateExerciseStatus.Created, (await handler.HandleAsync(TestUsers.B, "squat", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CreateExercise_LosingAConcurrentCreate_IsADuplicate()
    {
        var handler = new CreateExerciseHandler(_exercises, new FixedTimeProvider(Now));
        _exercises.BeforeAdd = () =>
        {
            _exercises.BeforeAdd = null;
            _exercises.Add(TestUsers.A, "squat");
        };

        var result = await handler.HandleAsync(TestUsers.A, "Squat", CancellationToken.None);

        Assert.Equal(CreateExerciseStatus.DuplicateName, result.Status);
        Assert.Single(_exercises.Exercises);
    }

    [Fact]
    public async Task GetExercises_ReturnsOnlyTheCallersExercises_ByName()
    {
        _exercises.Add(TestUsers.A, "squat");
        _exercises.Add(TestUsers.A, "Bench press");
        _exercises.Add(TestUsers.B, "Deadlift");

        var exercises = await new GetExercisesHandler(_exercises).HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Equal(["Bench press", "squat"], exercises.Select(exercise => exercise.Name));
    }

    // ---- Programs ----

    [Fact]
    public async Task CreateRenameListAndDelete_Program()
    {
        var created = await new CreateWorkoutProgramHandler(_programs, new FixedTimeProvider(Now))
            .HandleAsync(TestUsers.A, " Strength ", CancellationToken.None);
        await new CreateWorkoutProgramHandler(_programs, new FixedTimeProvider(Now))
            .HandleAsync(TestUsers.B, "Other", CancellationToken.None);

        var renamed = await new RenameWorkoutProgramHandler(_programs, _exercises)
            .HandleAsync(TestUsers.A, created.Id, "Hypertrophy", CancellationToken.None);

        Assert.Equal(("Hypertrophy", Now), (renamed.Program!.Name, renamed.Program.CreatedAtUtc));
        var listed = Assert.Single(await new GetWorkoutProgramsHandler(_programs).HandleAsync(TestUsers.A, CancellationToken.None));
        Assert.Equal((created.Id, "Hypertrophy", 0), (listed.Id, listed.Name, listed.WorkoutCount));

        Assert.True(await new DeleteWorkoutProgramHandler(_programs).HandleAsync(TestUsers.A, created.Id, CancellationToken.None));
        Assert.Empty(await new GetWorkoutProgramsHandler(_programs).HandleAsync(TestUsers.A, CancellationToken.None));
    }

    [Fact]
    public async Task AnotherUsersProgram_IsNotFound_ForEveryOperation_AndIsNotChanged()
    {
        var program = await NewProgramAsync(TestUsers.B, "Theirs");
        var workoutId = program.Workouts[0].Id;
        var bench = _exercises.Add(TestUsers.A, "Bench press");

        var results = new[]
        {
            await new RenameWorkoutProgramHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, "Mine", CancellationToken.None),
            await new AddWorkoutHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, "Day", CancellationToken.None),
            await new RenameWorkoutHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, workoutId, "Day", CancellationToken.None),
            await new DeleteWorkoutHandler(_programs, _exercises, new ActiveProgramProgress(new InMemoryActiveProgramRepository(), _programs, TimeProvider.System)).HandleAsync(TestUsers.A, program.Id, workoutId, CancellationToken.None),
            await new ReorderWorkoutsHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, [workoutId], CancellationToken.None),
            await new AddWorkoutBlockHandler(_programs, _exercises).HandleAsync(
                TestUsers.A,
                new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Single, 60, [Input(bench.Id, 3, 8)]),
                CancellationToken.None)
        };

        Assert.All(results, result => Assert.Equal(WorkoutProgramEditStatus.ProgramNotFound, result.Status));
        Assert.Null(await new GetWorkoutProgramHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, CancellationToken.None));
        Assert.False(await new DeleteWorkoutProgramHandler(_programs).HandleAsync(TestUsers.A, program.Id, CancellationToken.None));

        var stored = _programs.Stored(program.Id);
        Assert.Equal(("Theirs", 1), (stored.Name, stored.Workouts.Count));
        Assert.Equal(0, _programs.Saves);
    }

    // ---- Workouts ----

    [Fact]
    public async Task Workouts_AddRenameReorderDelete_PersistInOrder()
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        var add = new AddWorkoutHandler(_programs, _exercises);
        await add.HandleAsync(TestUsers.A, program.Id, "B", CancellationToken.None);
        var afterAdd = (await add.HandleAsync(TestUsers.A, program.Id, "C", CancellationToken.None)).Program!;
        var (a, b, c) = (afterAdd.Workouts[0].Id, afterAdd.Workouts[1].Id, afterAdd.Workouts[2].Id);

        await new RenameWorkoutHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, b, "Legs", CancellationToken.None);
        await new ReorderWorkoutsHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, [c, b, a], CancellationToken.None);
        var result = await new DeleteWorkoutHandler(_programs, _exercises, new ActiveProgramProgress(new InMemoryActiveProgramRepository(), _programs, TimeProvider.System)).HandleAsync(TestUsers.A, program.Id, c, CancellationToken.None);

        Assert.Equal([("Legs", 1), ("Day 1", 2)], result.Program!.Workouts.Select(workout => (workout.Name, workout.Position)));
        Assert.Equal(["Legs", "Day 1"], _programs.Stored(program.Id).Workouts.Select(workout => workout.Name));
    }

    [Fact]
    public async Task MissingWorkout_IsNotFound()
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        var missing = Guid.NewGuid();

        Assert.Equal(
            WorkoutProgramEditStatus.WorkoutNotFound,
            (await new RenameWorkoutHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, missing, "X", CancellationToken.None)).Status);
        Assert.Equal(
            WorkoutProgramEditStatus.WorkoutNotFound,
            (await new DeleteWorkoutHandler(_programs, _exercises, new ActiveProgramProgress(new InMemoryActiveProgramRepository(), _programs, TimeProvider.System)).HandleAsync(TestUsers.A, program.Id, missing, CancellationToken.None)).Status);
        Assert.Equal(0, _programs.Saves);
    }

    [Fact]
    public async Task ReorderWorkouts_WithAStaleList_IsRejectedAndNothingIsSaved()
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        await new AddWorkoutHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, "Second", CancellationToken.None);
        var saves = _programs.Saves;

        await Assert.ThrowsAsync<ArgumentException>(() => new ReorderWorkoutsHandler(_programs, _exercises)
            .HandleAsync(TestUsers.A, program.Id, [program.Workouts[0].Id], CancellationToken.None));

        Assert.Equal(saves, _programs.Saves);
    }

    // ---- Blocks ----

    [Fact]
    public async Task AddSingleAndSuperset_ReturnTheProgramWithExerciseNamesAndOrderedSets()
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        var workoutId = program.Workouts[0].Id;
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        var curl = _exercises.Add(TestUsers.A, "Curl");
        var handler = new AddWorkoutBlockHandler(_programs, _exercises);

        await handler.HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Single, 90,
                [new WorkoutBlockExerciseInput(bench.Id, "Pause", [new(12, 12), new(10, 10), new(8, 8)])]),
            CancellationToken.None);
        var result = await handler.HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Superset, 120, [Input(row.Id, 3, 10), Input(curl.Id, 3, 12)]),
            CancellationToken.None);

        Assert.Equal(WorkoutProgramEditStatus.Updated, result.Status);
        var blocks = result.Program!.Workouts[0].Blocks;
        Assert.Equal([(1, WorkoutBlockKind.Single, (int?)90), (2, WorkoutBlockKind.Superset, (int?)120)], blocks.Select(block => (block.Position, block.Kind, block.RestSeconds)));
        Assert.Equal(("Bench press", "Pause"), (blocks[0].Exercises[0].ExerciseName, blocks[0].Exercises[0].Notes));
        Assert.Equal([12, 10, 8], blocks[0].Exercises[0].Sets.Select(set => set.TargetMinReps));
        Assert.Equal([("Row", 1), ("Curl", 2)], blocks[1].Exercises.Select(exercise => (exercise.ExerciseName, exercise.Position)));
        Assert.Equal(2, _programs.Stored(program.Id).Workouts[0].Blocks.Count);
    }

    [Fact]
    public async Task AddBlock_WithAnotherUsersOrAMissingExercise_IsExerciseNotFound()
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        var workoutId = program.Workouts[0].Id;
        var mine = _exercises.Add(TestUsers.A, "Bench press");
        var theirs = _exercises.Add(TestUsers.B, "Row");
        var handler = new AddWorkoutBlockHandler(_programs, _exercises);

        var withTheirs = await handler.HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Superset, 60, [Input(mine.Id, 3, 8), Input(theirs.Id, 3, 8)]),
            CancellationToken.None);
        var withMissing = await handler.HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Single, 60, [Input(Guid.NewGuid(), 3, 8)]),
            CancellationToken.None);

        Assert.Equal(WorkoutProgramEditStatus.ExerciseNotFound, withTheirs.Status);
        Assert.Equal(WorkoutProgramEditStatus.ExerciseNotFound, withMissing.Status);
        Assert.Empty(_programs.Stored(program.Id).Workouts[0].Blocks);
    }

    [Theory]
    [InlineData(0, 8, 60)]
    [InlineData(10, 8, 60)]
    [InlineData(8, 8, 0)]
    [InlineData(8, 8, -60)]
    public async Task AddBlock_WithInvalidRepsOrRest_IsRejectedAndNothingIsSaved(int min, int max, int rest)
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        var bench = _exercises.Add(TestUsers.A, "Bench press");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new AddWorkoutBlockHandler(_programs, _exercises).HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, program.Workouts[0].Id, WorkoutBlockKind.Single, rest,
                [new WorkoutBlockExerciseInput(bench.Id, null, [new(min, max)])]),
            CancellationToken.None));

        Assert.Empty(_programs.Stored(program.Id).Workouts[0].Blocks);
        Assert.Equal(0, _programs.Saves);
    }

    [Fact]
    public async Task AddBlock_WithoutSetsOrExercises_IsRejected()
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var handler = new AddWorkoutBlockHandler(_programs, _exercises);
        var workoutId = program.Workouts[0].Id;

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Single, 60, [new WorkoutBlockExerciseInput(bench.Id, null, null)]),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Single, 60, null),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Superset, 60, [Input(bench.Id, 3, 8)]),
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateReorderAndDeleteBlocks()
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        var workoutId = program.Workouts[0].Id;
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        var row = _exercises.Add(TestUsers.A, "Row");
        var add = new AddWorkoutBlockHandler(_programs, _exercises);
        await add.HandleAsync(TestUsers.A, new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Single, 60, [Input(bench.Id, 3, 8)]), CancellationToken.None);
        var added = (await add.HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, workoutId, WorkoutBlockKind.Superset, 90, [Input(bench.Id, 3, 8), Input(row.Id, 3, 8)]),
            CancellationToken.None)).Program!;
        var (singleId, supersetId) = (added.Workouts[0].Blocks[0].Id, added.Workouts[0].Blocks[1].Id);

        // Swap A and B, change rest and sets.
        var updated = await new UpdateWorkoutBlockHandler(_programs, _exercises).HandleAsync(
            TestUsers.A,
            new UpdateWorkoutBlockCommand(program.Id, workoutId, supersetId, 120, [Input(row.Id, 4, 10), Input(bench.Id, 2, 6)]),
            CancellationToken.None);
        var superset = updated.Program!.Workouts[0].Blocks[1];
        Assert.Equal((WorkoutBlockKind.Superset, (int?)120), (superset.Kind, superset.RestSeconds));
        Assert.Equal([("Row", 4), ("Bench press", 2)], superset.Exercises.Select(exercise => (exercise.ExerciseName, exercise.Sets.Count)));

        await new ReorderWorkoutBlocksHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, workoutId, [supersetId, singleId], CancellationToken.None);
        var deleted = await new DeleteWorkoutBlockHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, workoutId, supersetId, CancellationToken.None);

        var remaining = Assert.Single(deleted.Program!.Workouts[0].Blocks);
        Assert.Equal((singleId, 1), (remaining.Id, remaining.Position));
        Assert.Equal(
            WorkoutProgramEditStatus.BlockNotFound,
            (await new DeleteWorkoutBlockHandler(_programs, _exercises).HandleAsync(TestUsers.A, program.Id, workoutId, supersetId, CancellationToken.None)).Status);
        Assert.Equal(
            WorkoutProgramEditStatus.BlockNotFound,
            (await new UpdateWorkoutBlockHandler(_programs, _exercises).HandleAsync(
                TestUsers.A,
                new UpdateWorkoutBlockCommand(program.Id, workoutId, supersetId, 60, [Input(bench.Id, 1, 8)]),
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task DeletingAProgram_KeepsItsExercises()
    {
        var program = await NewProgramAsync(TestUsers.A, "Program");
        var bench = _exercises.Add(TestUsers.A, "Bench press");
        await new AddWorkoutBlockHandler(_programs, _exercises).HandleAsync(
            TestUsers.A,
            new AddWorkoutBlockCommand(program.Id, program.Workouts[0].Id, WorkoutBlockKind.Single, 60, [Input(bench.Id, 3, 8)]),
            CancellationToken.None);

        Assert.True(await new DeleteWorkoutProgramHandler(_programs).HandleAsync(TestUsers.A, program.Id, CancellationToken.None));

        Assert.Empty(_programs.Programs);
        Assert.Single(await new GetExercisesHandler(_exercises).HandleAsync(TestUsers.A, CancellationToken.None));
    }

    private async Task<WorkoutProgram> NewProgramAsync(Guid userId, string name)
    {
        var program = WorkoutProgram.Create(userId, name, Now);
        program.AddWorkout("Day 1");
        await _programs.AddAsync(program, CancellationToken.None);

        return program;
    }

    private static WorkoutBlockExerciseInput Input(Guid exerciseId, int sets, int reps) =>
        new(exerciseId, null, Enumerable.Repeat(new SetTargetInput(reps, reps), sets).ToList());
}
