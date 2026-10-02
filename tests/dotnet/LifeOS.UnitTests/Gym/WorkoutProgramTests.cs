using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Gym;

public class WorkoutProgramTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly Exercise Bench = Exercise.Create(TestUsers.A, "Bench press", Now);
    private static readonly Exercise Row = Exercise.Create(TestUsers.A, "Row", Now);

    // ---- Program ----

    [Fact]
    public void Create_TrimsTheName_AndStartsEmpty()
    {
        var program = WorkoutProgram.Create(TestUsers.A, "  Hypertrophy  ", Now.ToOffset(TimeSpan.FromHours(2)));

        Assert.NotEqual(Guid.Empty, program.Id);
        Assert.Equal((TestUsers.A, "Hypertrophy", TimeSpan.Zero), (program.UserId, program.Name, program.CreatedAtUtc.Offset));
        Assert.Empty(program.Workouts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_And_Rename_RejectABlankName(string name)
    {
        Assert.Throws<ArgumentException>(() => WorkoutProgram.Create(TestUsers.A, name, Now));

        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);
        Assert.Throws<ArgumentException>(() => program.Rename(name));
        Assert.Equal("Program", program.Name);
    }

    [Fact]
    public void Create_RejectsAnEmptyUser() =>
        Assert.Throws<ArgumentException>(() => WorkoutProgram.Create(Guid.Empty, "Program", Now));

    // ---- Workouts ----

    [Fact]
    public void AddWorkout_AppendsWithTheNextPosition()
    {
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);

        var first = program.AddWorkout("Back / Triceps / Legs");
        var second = program.AddWorkout(" Shoulders / Chest / Biceps ");

        Assert.Equal([(first.Id, 1), (second.Id, 2)], program.Workouts.Select(workout => (workout.Id, workout.Position)));
        Assert.Equal("Shoulders / Chest / Biceps", second.Name);
        Assert.Equal((program.Id, TestUsers.A), (second.WorkoutProgramId, second.UserId));
    }

    [Fact]
    public void RemoveWorkout_ClosesTheGap_AndReportsAMissingWorkout()
    {
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);
        var a = program.AddWorkout("A");
        var b = program.AddWorkout("B");
        var c = program.AddWorkout("C");

        Assert.True(program.RemoveWorkout(b.Id));
        Assert.False(program.RemoveWorkout(b.Id));

        Assert.Equal([(a.Id, 1), (c.Id, 2)], program.Workouts.Select(workout => (workout.Id, workout.Position)));
    }

    [Fact]
    public void ReorderWorkouts_AppliesThePermutation()
    {
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);
        var a = program.AddWorkout("A");
        var b = program.AddWorkout("B");
        var c = program.AddWorkout("C");

        program.ReorderWorkouts([c.Id, a.Id, b.Id]);

        Assert.Equal(["C", "A", "B"], program.Workouts.Select(workout => workout.Name));
        Assert.Equal([1, 2, 3], program.Workouts.Select(workout => workout.Position));
    }

    [Fact]
    public void ReorderWorkouts_RejectsAnythingButAPermutation_AndChangesNothing()
    {
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);
        var a = program.AddWorkout("A");
        var b = program.AddWorkout("B");

        Assert.Throws<ArgumentException>(() => program.ReorderWorkouts([b.Id]));
        Assert.Throws<ArgumentException>(() => program.ReorderWorkouts([b.Id, b.Id]));
        Assert.Throws<ArgumentException>(() => program.ReorderWorkouts([b.Id, a.Id, Guid.NewGuid()]));
        Assert.Throws<ArgumentException>(() => program.ReorderWorkouts([b.Id, Guid.NewGuid()]));
        Assert.Throws<ArgumentException>(() => program.ReorderWorkouts(null!));

        Assert.Equal(["A", "B"], program.Workouts.Select(workout => workout.Name));
    }

    // ---- Blocks ----

    [Fact]
    public void SingleBlock_HasExactlyOneExercise_WithOrderedSets()
    {
        var workout = NewWorkout();

        var block = workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(Bench, "  Pause  ", (12, 12), (10, 10), (8, 8))]);

        Assert.Equal((1, WorkoutBlockKind.Single, (int?)90), (block.Position, block.Kind, block.RestSeconds));
        var exercise = Assert.Single(block.Exercises);
        Assert.Equal((1, Bench.Id, "Pause"), (exercise.Position, exercise.ExerciseId, exercise.Notes));
        Assert.Equal([(1, 12, 12), (2, 10, 10), (3, 8, 8)], exercise.Sets.Select(set => (set.Position, set.TargetMinReps, set.TargetMaxReps)));
    }

    [Fact]
    public void Superset_HasExactlyTwoExercises_InABOrder()
    {
        var workout = NewWorkout();

        var block = workout.AddBlock(WorkoutBlockKind.Superset, 120, [Prescription(Row, null, (8, 10)), Prescription(Bench, null, (12, 12))]);

        Assert.Equal([(1, Row.Id), (2, Bench.Id)], block.Exercises.Select(exercise => (exercise.Position, exercise.ExerciseId)));
        Assert.Equal(120, block.RestSeconds);
    }

    [Theory]
    [InlineData(WorkoutBlockKind.Single, 0)]
    [InlineData(WorkoutBlockKind.Single, 2)]
    [InlineData(WorkoutBlockKind.Superset, 1)]
    [InlineData(WorkoutBlockKind.Superset, 3)]
    public void AddBlock_WithTheWrongNumberOfExercises_IsRejected(WorkoutBlockKind kind, int count)
    {
        var workout = NewWorkout();

        var exception = Assert.Throws<ArgumentException>(() =>
            workout.AddBlock(kind, 60, Enumerable.Repeat(Prescription(Bench, null, (8, 8)), count).ToList()));

        Assert.Equal("exercises", exception.ParamName);
        Assert.Empty(workout.Blocks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(WorkoutBlock.MaxRestSeconds + 1)]
    public void AddBlock_WithInvalidRest_IsRejected(int restSeconds)
    {
        var workout = NewWorkout();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            workout.AddBlock(WorkoutBlockKind.Single, restSeconds, [Prescription(Bench, null, (8, 8))]));

        Assert.Equal("restSeconds", exception.ParamName);
        Assert.Empty(workout.Blocks);
    }

    [Fact]
    public void AddBlock_WithoutRest_IsAllowed()
    {
        var block = NewWorkout().AddBlock(WorkoutBlockKind.Single, null, [Prescription(Bench, null, (8, 8))]);

        Assert.Null(block.RestSeconds);
    }

    [Fact]
    public void AddBlock_WithoutSets_IsRejected()
    {
        var workout = NewWorkout();

        var exception = Assert.Throws<ArgumentException>(() =>
            workout.AddBlock(WorkoutBlockKind.Single, 60, [new ExercisePrescription(Bench, null, [])]));

        Assert.Equal("sets", exception.ParamName);
    }

    [Fact]
    public void AddBlock_WithTooManySets_IsRejected()
    {
        var sets = Enumerable.Repeat((8, 8), WorkoutBlockExercise.MaxSets + 1).ToArray();

        Assert.Throws<ArgumentException>(() =>
            NewWorkout().AddBlock(WorkoutBlockKind.Single, 60, [Prescription(Bench, null, sets)]));
    }

    [Fact]
    public void AddBlock_WithAnotherUsersExercise_IsRejected()
    {
        var othersExercise = Exercise.Create(TestUsers.B, "Squat", Now);

        Assert.Throws<ArgumentException>(() =>
            NewWorkout().AddBlock(WorkoutBlockKind.Single, 60, [Prescription(othersExercise, null, (5, 5))]));
    }

    [Fact]
    public void AddBlock_WithTooLongNotes_IsRejected()
    {
        var notes = new string('x', WorkoutBlockExercise.MaxNotesLength + 1);

        var exception = Assert.Throws<ArgumentException>(() =>
            NewWorkout().AddBlock(WorkoutBlockKind.Single, 60, [Prescription(Bench, notes, (8, 8))]));

        Assert.Equal("notes", exception.ParamName);
    }

    [Fact]
    public void BlankNotes_AreStoredAsNone()
    {
        var block = NewWorkout().AddBlock(WorkoutBlockKind.Single, 60, [Prescription(Bench, "   ", (8, 8))]);

        Assert.Null(block.Exercises[0].Notes);
    }

    [Fact]
    public void RemoveAndReorderBlocks_KeepPositionsContiguous()
    {
        var workout = NewWorkout();
        var a = workout.AddBlock(WorkoutBlockKind.Single, 60, [Prescription(Bench, null, (8, 8))]);
        var b = workout.AddBlock(WorkoutBlockKind.Single, 60, [Prescription(Row, null, (8, 8))]);
        var c = workout.AddBlock(WorkoutBlockKind.Superset, 60, [Prescription(Row, null, (8, 8)), Prescription(Bench, null, (8, 8))]);

        workout.ReorderBlocks([c.Id, b.Id, a.Id]);
        Assert.Equal([(c.Id, 1), (b.Id, 2), (a.Id, 3)], workout.Blocks.Select(block => (block.Id, block.Position)));

        Assert.True(workout.RemoveBlock(b.Id));
        Assert.False(workout.RemoveBlock(b.Id));
        Assert.Equal([(c.Id, 1), (a.Id, 2)], workout.Blocks.Select(block => (block.Id, block.Position)));

        Assert.Throws<ArgumentException>(() => workout.ReorderBlocks([a.Id]));
    }

    // ---- Block update ----

    [Fact]
    public void Update_ChangesRestExerciseNotesAndSets_KeepingSlotAndSetIdentities()
    {
        var block = NewWorkout().AddBlock(WorkoutBlockKind.Single, 60, [Prescription(Bench, "Old", (8, 8), (8, 8), (8, 8))]);
        var slotId = block.Exercises[0].Id;
        var setIds = block.Exercises[0].Sets.Select(set => set.Id).ToList();

        block.Update(90, [Prescription(Row, "New", (12, 12), (10, 10))]);

        var exercise = Assert.Single(block.Exercises);
        Assert.Equal((slotId, Row.Id, "New"), (exercise.Id, exercise.ExerciseId, exercise.Notes));
        Assert.Equal(90, block.RestSeconds);
        Assert.Equal([(setIds[0], 1, 12), (setIds[1], 2, 10)], exercise.Sets.Select(set => (set.Id, set.Position, set.TargetMinReps)));

        block.Update(null, [Prescription(Row, null, (12, 12), (10, 10), (8, 8), (6, 6))]);

        Assert.Equal([1, 2, 3, 4], block.Exercises[0].Sets.Select(set => set.Position));
        Assert.Equal([12, 10, 8, 6], block.Exercises[0].Sets.Select(set => set.TargetMinReps));
        Assert.Null(block.RestSeconds);
    }

    [Fact]
    public void Update_ThatIsInvalid_ChangesNothing()
    {
        var block = NewWorkout().AddBlock(WorkoutBlockKind.Superset, 60, [Prescription(Bench, "A", (8, 8)), Prescription(Row, "B", (8, 8))]);

        Assert.Throws<ArgumentException>(() => block.Update(90, [Prescription(Row, "x", (10, 10))]));
        Assert.Throws<ArgumentOutOfRangeException>(() => block.Update(0, [Prescription(Row, "x", (10, 10)), Prescription(Bench, "y", (10, 10))]));
        Assert.Throws<ArgumentException>(() => block.Update(90, [Prescription(Row, "x", (10, 10)), new ExercisePrescription(Bench, "y", [])]));

        Assert.Equal(60, block.RestSeconds);
        Assert.Equal([(Bench.Id, "A"), (Row.Id, "B")], block.Exercises.Select(exercise => (exercise.ExerciseId, exercise.Notes)));
    }

    // ---- Rep ranges ----

    [Theory]
    [InlineData(8, 8)]
    [InlineData(8, 10)]
    [InlineData(1, RepRange.MaxReps)]
    public void RepRange_AcceptsAPositiveRange(int min, int max)
    {
        var range = new RepRange(min, max);

        Assert.Equal((min, max), (range.TargetMinReps, range.TargetMaxReps));
    }

    [Theory]
    [InlineData(0, 8, "targetMinReps")]
    [InlineData(-1, 8, "targetMinReps")]
    [InlineData(10, 8, "targetMaxReps")]
    [InlineData(8, RepRange.MaxReps + 1, "targetMaxReps")]
    public void RepRange_RejectsInvalidReps(int min, int max, string parameter)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new RepRange(min, max));

        Assert.Equal(parameter, exception.ParamName);
    }

    // ---- Exercise ----

    [Fact]
    public void Exercise_TrimsTheName_AndRejectsABlankOne()
    {
        Assert.Equal("Bench press", Exercise.Create(TestUsers.A, "  Bench press ", Now).Name);
        Assert.Throws<ArgumentException>(() => Exercise.Create(TestUsers.A, " ", Now));
        Assert.Throws<ArgumentException>(() => Exercise.Create(Guid.Empty, "Squat", Now));
    }

    private static WorkoutTemplate NewWorkout() =>
        WorkoutProgram.Create(TestUsers.A, "Program", Now).AddWorkout("Day 1");

    private static ExercisePrescription Prescription(Exercise exercise, string? notes, params (int Min, int Max)[] sets) =>
        new(exercise, notes, sets.Select(set => new RepRange(set.Min, set.Max)).ToList());
}
