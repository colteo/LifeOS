using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Gym;

public class WorkoutSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    private readonly Exercise _bench = Exercise.Create(TestUsers.A, "Bench press", Now);
    private readonly Exercise _row = Exercise.Create(TestUsers.A, "Row", Now);
    private readonly Exercise _curl = Exercise.Create(TestUsers.A, "Curl", Now);

    // ---- Snapshot ----

    [Fact]
    public void Start_SnapshotsThePrescription_InOrder()
    {
        var (program, workout) = Workout();
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(_bench, "Pause", (8, 8), (8, 8), (8, 8))]);
        workout.AddBlock(WorkoutBlockKind.Superset, 120, [Prescription(_row, null, (8, 10), (8, 10)), Prescription(_curl, null, (12, 12), (12, 12))]);

        var session = WorkoutSession.Start(program, workout, Now);

        Assert.Equal((TestUsers.A, program.Id, workout.Id), (session.UserId, session.WorkoutProgramId, session.WorkoutTemplateId));
        Assert.Equal(("Program", "Push"), (session.ProgramName, session.WorkoutName));
        Assert.Equal((WorkoutSessionStatus.InProgress, Now, (DateTimeOffset?)null), (session.Status, session.StartedAtUtc, session.CompletedAtUtc));

        Assert.Equal([(1, WorkoutBlockKind.Single, (int?)90), (2, WorkoutBlockKind.Superset, (int?)120)],
            session.Blocks.Select(block => (block.Position, block.Kind, block.RestSeconds)));
        var single = Assert.Single(session.Blocks[0].Exercises);
        Assert.Equal((_bench.Id, "Pause"), (single.ExerciseId, single.Notes));
        Assert.Equal([(1, 8, 8), (2, 8, 8), (3, 8, 8)], single.Sets.Select(set => (set.Position, set.TargetMinReps, set.TargetMaxReps)));
        Assert.Equal([(1, _row.Id), (2, _curl.Id)], session.Blocks[1].Exercises.Select(exercise => (exercise.Position, exercise.ExerciseId)));
        Assert.Equal([(8, 10), (8, 10)], session.Blocks[1].Exercises[0].Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps)));
        Assert.All(session.ExecutionOrder, set => Assert.Null(set.CompletedAtUtc));
        Assert.Equal((0, 7), (session.CompletedSetCount, session.PrescribedSetCount));
    }

    [Fact]
    public void TemplateChanges_AfterStart_DoNotChangeTheSnapshot()
    {
        var (program, workout) = Workout();
        var block = workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(_bench, null, (8, 8), (8, 8), (8, 8))]);
        var session = WorkoutSession.Start(program, workout, Now);

        // October 10: the trainer changes Bench Press to 4 × 10 and renames the workout.
        block.Update(60, [Prescription(_row, "Changed", (10, 10), (10, 10), (10, 10), (10, 10))]);
        workout.Rename("Renamed");
        program.RemoveWorkout(workout.Id);

        var exercise = Assert.Single(session.Blocks[0].Exercises);
        Assert.Equal((_bench.Id, (string?)null, (int?)90), (exercise.ExerciseId, exercise.Notes, session.Blocks[0].RestSeconds));
        Assert.Equal([(8, 8), (8, 8), (8, 8)], exercise.Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps)));
        Assert.Equal("Push", session.WorkoutName);
    }

    [Fact]
    public void StartingTheSameWorkoutAgain_CreatesAnIndependentSession()
    {
        var (program, workout) = Workout();
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(_bench, null, (8, 8))]);

        var first = WorkoutSession.Start(program, workout, Now);
        var second = WorkoutSession.Start(program, workout, Now.AddDays(3));

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.ExecutionOrder[0].Id, second.ExecutionOrder[0].Id);
    }

    [Fact]
    public void Start_AWorkoutWithoutBlocks_IsRejected()
    {
        var (program, workout) = Workout();

        var exception = Assert.Throws<ArgumentException>(() => WorkoutSession.Start(program, workout, Now));

        Assert.Equal("workoutId", exception.ParamName);
    }

    [Fact]
    public void Start_AWorkoutOfAnotherProgram_IsRejected()
    {
        var (_, workout) = Workout();
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(_bench, null, (8, 8))]);
        var other = WorkoutProgram.Create(TestUsers.A, "Other", Now);

        Assert.Throws<ArgumentException>(() => WorkoutSession.Start(other, workout, Now));
    }

    // ---- Execution order ----

    [Fact]
    public void ExecutionOrder_Single_IsSetBySet()
    {
        var session = SingleSession(sets: 3, rest: 90);

        Assert.Equal([1, 2, 3], session.ExecutionOrder.Select(set => set.Position));
    }

    [Fact]
    public void ExecutionOrder_Superset_AlternatesAAndB_AndKeepsTheExtraSetsOfTheLongerExercise()
    {
        var (program, workout) = Workout();
        workout.AddBlock(WorkoutBlockKind.Superset, 120, [Prescription(_row, null, (8, 8), (8, 8), (8, 8), (8, 8)), Prescription(_curl, null, (12, 12), (12, 12), (12, 12))]);
        var session = WorkoutSession.Start(program, workout, Now);
        var (a, b) = (session.Blocks[0].Exercises[0], session.Blocks[0].Exercises[1]);

        Assert.Equal(
            [a.Sets[0].Id, b.Sets[0].Id, a.Sets[1].Id, b.Sets[1].Id, a.Sets[2].Id, b.Sets[2].Id, a.Sets[3].Id],
            session.ExecutionOrder.Select(set => set.Id));
        Assert.Equal(7, session.PrescribedSetCount);
    }

    // ---- Recording sets ----

    [Fact]
    public void RecordSet_CompletesTheSet_WithTheGivenTime_AndOptionalLoad()
    {
        var session = SingleSession(sets: 2, rest: 90);
        var (first, second) = (session.ExecutionOrder[0], session.ExecutionOrder[1]);

        session.RecordSet(first.Id, 8, 82.5m, Now.AddMinutes(1));
        session.RecordSet(second.Id, 12, null, Now.AddMinutes(3));

        Assert.Equal((8, (decimal?)82.5m, (DateTimeOffset?)Now.AddMinutes(1)), (first.ActualReps, first.WeightKg, first.CompletedAtUtc));
        Assert.Equal((12, (decimal?)null), (second.ActualReps, second.WeightKg));
        Assert.Equal((8, 8), (first.TargetMinReps, first.TargetMaxReps));
        Assert.Equal(2, session.CompletedSetCount);
    }

    [Fact]
    public void RecordSet_AgainWhileInProgress_CorrectsIt_AndKeepsTheOriginalCompletionTime()
    {
        var session = SingleSession(sets: 2, rest: 90);
        var set = session.ExecutionOrder[0];
        session.RecordSet(set.Id, 8, 80m, Now.AddMinutes(1));

        session.RecordSet(set.Id, 7, 77.5m, Now.AddMinutes(5));

        Assert.Equal((7, (decimal?)77.5m, (DateTimeOffset?)Now.AddMinutes(1)), (set.ActualReps, set.WeightKg, set.CompletedAtUtc));
    }

    [Theory]
    [InlineData(0, null, "actualReps")]
    [InlineData(-1, null, "actualReps")]
    [InlineData(1000, null, "actualReps")]
    [InlineData(8, "0", "weightKg")]
    [InlineData(8, "-5", "weightKg")]
    [InlineData(8, "1000.01", "weightKg")]
    [InlineData(8, "82.555", "weightKg")]
    public void RecordSet_RejectsInvalidRepsOrWeight_AndChangesNothing(int reps, string? weight, string parameter)
    {
        var session = SingleSession(sets: 1, rest: 90);
        var set = session.ExecutionOrder[0];

        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            session.RecordSet(set.Id, reps, weight is null ? null : decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture), Now));

        Assert.Equal(parameter, exception.ParamName);
        Assert.Null(set.CompletedAtUtc);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("1000")]
    [InlineData("2.25")]
    public void RecordSet_AcceptsTheWeightBounds(string weight)
    {
        var session = SingleSession(sets: 1, rest: 90);

        var set = session.RecordSet(session.ExecutionOrder[0].Id, 1, decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture), Now);

        Assert.True(set.IsCompleted);
    }

    [Fact]
    public void RecordSet_UnknownSet_IsRejected()
    {
        var session = SingleSession(sets: 1, rest: 90);

        Assert.False(session.HasSet(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => session.RecordSet(Guid.NewGuid(), 8, null, Now));
    }

    // ---- Finish / discard ----

    [Fact]
    public void Finish_WithEverySetCompleted()
    {
        var session = SingleSession(sets: 2, rest: 90);
        session.RecordSet(session.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(2));
        session.RecordSet(session.ExecutionOrder[1].Id, 8, 80m, Now.AddMinutes(5));

        session.Finish(Now.AddMinutes(42).AddSeconds(30));

        Assert.Equal(WorkoutSessionStatus.Completed, session.Status);
        Assert.Equal(Now.AddMinutes(42).AddSeconds(30), session.CompletedAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(42.5), session.Duration);
        Assert.Equal((2, 2), (session.CompletedSetCount, session.PrescribedSetCount));
    }

    [Fact]
    public void Finish_WithSomeSetsIncomplete_IsAllowed_AndTheyStayPending()
    {
        var session = SingleSession(sets: 3, rest: 90);
        session.RecordSet(session.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(2));

        session.Finish(Now.AddMinutes(10));

        Assert.Equal((WorkoutSessionStatus.Completed, 1, 3), (session.Status, session.CompletedSetCount, session.PrescribedSetCount));
        Assert.Null(session.ExecutionOrder[2].CompletedAtUtc);
    }

    [Fact]
    public void Duration_IsNullWhileInProgress()
    {
        Assert.Null(SingleSession(sets: 1, rest: 90).Duration);
    }

    [Fact]
    public void CompletedSession_IsImmutable_AndCannotBeDiscarded()
    {
        var session = SingleSession(sets: 2, rest: 90);
        var set = session.ExecutionOrder[0];
        session.RecordSet(set.Id, 8, 80m, Now.AddMinutes(1));
        session.Finish(Now.AddMinutes(10));

        Assert.Throws<InvalidOperationException>(() => session.RecordSet(set.Id, 10, 100m, Now.AddMinutes(11)));
        Assert.Throws<InvalidOperationException>(() => session.RecordSet(session.ExecutionOrder[1].Id, 10, 100m, Now.AddMinutes(11)));
        Assert.Throws<InvalidOperationException>(() => session.Finish(Now.AddMinutes(12)));
        Assert.Throws<InvalidOperationException>(session.EnsureCanDiscard);
        Assert.Equal((8, (decimal?)80m, (DateTimeOffset?)Now.AddMinutes(10)), (set.ActualReps, set.WeightKg, session.CompletedAtUtc));
    }

    [Fact]
    public void InProgressSession_CanBeDiscarded()
    {
        SingleSession(sets: 1, rest: 90).EnsureCanDiscard();
    }

    // ---- Rest ----

    [Fact]
    public void Rest_Single_StartsAfterEachSet_WhileAnotherSetRemains()
    {
        var session = SingleSession(sets: 2, rest: 90);
        Assert.Null(session.CurrentRest());

        session.RecordSet(session.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1));
        Assert.Equal(new RestPeriod(Now.AddMinutes(1), 90), session.CurrentRest());
        Assert.Equal(Now.AddMinutes(1).AddSeconds(90), session.CurrentRest()!.EndsAtUtc);

        // The last set of the workout: nothing remains, so no rest.
        session.RecordSet(session.ExecutionOrder[1].Id, 8, 80m, Now.AddMinutes(4));
        Assert.Null(session.CurrentRest());
    }

    [Fact]
    public void Rest_Superset_StartsOnlyAfterBOfTheRound()
    {
        var session = SupersetSession(aSets: 2, bSets: 2, rest: 120);
        var (a, b) = (session.Blocks[0].Exercises[0], session.Blocks[0].Exercises[1]);

        session.RecordSet(a.Sets[0].Id, 10, 60m, Now.AddMinutes(1));
        Assert.Null(session.CurrentRest());

        session.RecordSet(b.Sets[0].Id, 12, 20m, Now.AddMinutes(2));
        Assert.Equal(new RestPeriod(Now.AddMinutes(2), 120), session.CurrentRest());

        session.RecordSet(a.Sets[1].Id, 10, 60m, Now.AddMinutes(5));
        Assert.Null(session.CurrentRest());
    }

    [Fact]
    public void Rest_Superset_RecordedOutOfOrder_StartsWhenTheRoundIsComplete()
    {
        var session = SupersetSession(aSets: 2, bSets: 2, rest: 120);
        var (a, b) = (session.Blocks[0].Exercises[0], session.Blocks[0].Exercises[1]);

        session.RecordSet(b.Sets[0].Id, 12, 20m, Now.AddMinutes(1));
        Assert.Null(session.CurrentRest());

        session.RecordSet(a.Sets[0].Id, 10, 60m, Now.AddMinutes(2));
        Assert.Equal(new RestPeriod(Now.AddMinutes(2), 120), session.CurrentRest());
    }

    [Fact]
    public void Rest_Superset_WithUnevenSets_TheLoneExtraSetEndsItsRound()
    {
        var session = SupersetSession(aSets: 2, bSets: 1, rest: 120, followedBySingle: true);
        var a = session.Blocks[0].Exercises[0];
        var b = session.Blocks[0].Exercises[1];

        session.RecordSet(a.Sets[0].Id, 10, 60m, Now.AddMinutes(1));
        session.RecordSet(b.Sets[0].Id, 12, 20m, Now.AddMinutes(2));
        session.RecordSet(a.Sets[1].Id, 10, 60m, Now.AddMinutes(5));

        Assert.Equal(new RestPeriod(Now.AddMinutes(5), 120), session.CurrentRest());
    }

    [Fact]
    public void Rest_AfterTheLastSetOfABlock_UsesThatBlocksRest_BeforeTheNextBlock()
    {
        var (program, workout) = Workout();
        workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(_bench, null, (8, 8))]);
        workout.AddBlock(WorkoutBlockKind.Single, 60, [Prescription(_row, null, (8, 8))]);
        var session = WorkoutSession.Start(program, workout, Now);

        session.RecordSet(session.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1));

        Assert.Equal(new RestPeriod(Now.AddMinutes(1), 90), session.CurrentRest());
    }

    [Fact]
    public void Rest_IsNone_WithoutPrescribedRest_OrOnceFinished()
    {
        var noRest = SingleSession(sets: 2, rest: null);
        noRest.RecordSet(noRest.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1));
        Assert.Null(noRest.CurrentRest());

        var finished = SingleSession(sets: 2, rest: 90);
        finished.RecordSet(finished.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1));
        finished.Finish(Now.AddMinutes(2));
        Assert.Null(finished.CurrentRest());
    }

    [Fact]
    public void Rest_FollowsTheMostRecentCompletion_NotACorrection()
    {
        var session = SingleSession(sets: 3, rest: 90);
        session.RecordSet(session.ExecutionOrder[0].Id, 8, 80m, Now.AddMinutes(1));
        session.RecordSet(session.ExecutionOrder[1].Id, 8, 80m, Now.AddMinutes(4));

        // Correcting set 1 later neither restarts nor moves the rest.
        session.RecordSet(session.ExecutionOrder[0].Id, 9, 80m, Now.AddMinutes(5));

        Assert.Equal(new RestPeriod(Now.AddMinutes(4), 90), session.CurrentRest());
    }

    // ---- Helpers ----

    private static (WorkoutProgram Program, WorkoutTemplate Workout) Workout()
    {
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);

        return (program, program.AddWorkout("Push"));
    }

    private WorkoutSession SingleSession(int sets, int? rest)
    {
        var (program, workout) = Workout();
        workout.AddBlock(WorkoutBlockKind.Single, rest, [Prescription(_bench, null, Enumerable.Repeat((8, 8), sets).ToArray())]);

        return WorkoutSession.Start(program, workout, Now);
    }

    private WorkoutSession SupersetSession(int aSets, int bSets, int rest, bool followedBySingle = false)
    {
        var (program, workout) = Workout();
        workout.AddBlock(WorkoutBlockKind.Superset, rest,
            [Prescription(_row, null, Enumerable.Repeat((10, 10), aSets).ToArray()), Prescription(_curl, null, Enumerable.Repeat((12, 12), bSets).ToArray())]);

        if (followedBySingle)
        {
            workout.AddBlock(WorkoutBlockKind.Single, 90, [Prescription(_bench, null, (8, 8))]);
        }

        return WorkoutSession.Start(program, workout, Now);
    }

    private static ExercisePrescription Prescription(Exercise exercise, string? notes, params (int Min, int Max)[] sets) =>
        new(exercise, notes, sets.Select(set => new RepRange(set.Min, set.Max)).ToList());
}
