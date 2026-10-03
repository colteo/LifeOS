using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Gym.Training;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Gym;

// GYM-004: the active program's cycle rules.
public class ActiveProgramTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);

    private readonly Exercise _bench = Exercise.Create(TestUsers.A, "Bench press", Now);

    // ---- Activation ----

    [Fact]
    public void Activate_StartsAtCycleOne_WithNothingDone()
    {
        var program = Program("Day 1", "Day 2", "Day 3");

        var active = ActiveProgram.Activate(program, 5, Now);

        Assert.Equal((TestUsers.A, program.Id, 5, 1), (active.UserId, active.WorkoutProgramId, active.TotalCycles, active.CurrentCycle));
        Assert.Equal((ActiveProgramStatus.Active, Now, (DateTimeOffset?)null), (active.Status, active.ActivatedAtUtc, active.EndedAtUtc));
        Assert.Empty(active.Completions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ActiveProgram.MaxCycles + 1)]
    public void Activate_RefusesAnInvalidNumberOfCycles(int cycles)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActiveProgram.Activate(Program("Day 1"), cycles, Now));
    }

    [Fact]
    public void Activate_RefusesAProgramThatCouldNeverCompleteACycle()
    {
        Assert.Throws<ArgumentException>(() => ActiveProgram.Activate(WorkoutProgram.Create(TestUsers.A, "Empty", Now), 3, Now));

        var withEmptyWorkout = Program("Day 1");
        withEmptyWorkout.AddWorkout("Day 2");
        Assert.Throws<ArgumentException>(() => ActiveProgram.Activate(withEmptyWorkout, 3, Now));
    }

    // ---- Progress ----

    [Fact]
    public void Workouts_CountOncePerCycle_InAnyOrder_ThenTheNextCycleOpens()
    {
        var program = Program("Day 1", "Day 2", "Day 3");
        var (day1, day2, day3) = (program.Workouts[0], program.Workouts[1], program.Workouts[2]);
        var active = ActiveProgram.Activate(program, 2, Now);

        Assert.True(active.RecordWorkout(program, Finished(program, day3, 1), Now.AddDays(1)));
        Assert.True(active.RecordWorkout(program, Finished(program, day1, 2), Now.AddDays(2)));

        Assert.Equal(1, active.CurrentCycle);
        Assert.True(active.IsCompletedInCurrentCycle(day3.Id));
        Assert.False(active.IsCompletedInCurrentCycle(day2.Id));

        // A repeat of a workout already done in this cycle is training, not progress.
        Assert.False(active.RecordWorkout(program, Finished(program, day1, 3), Now.AddDays(3)));
        Assert.Equal(2, active.CurrentCycleCompletions.Count);

        Assert.True(active.RecordWorkout(program, Finished(program, day2, 4), Now.AddDays(4)));

        Assert.Equal((2, ActiveProgramStatus.Active), (active.CurrentCycle, active.Status));
        Assert.Empty(active.CurrentCycleCompletions);
        Assert.Equal([1, 1, 1], active.Completions.Select(completion => completion.Cycle));
    }

    [Fact]
    public void LastWorkoutOfTheLastCycle_CompletesTheProgram()
    {
        var program = Program("Day 1", "Day 2");
        var active = ActiveProgram.Activate(program, 2, Now);

        foreach (var (workout, day) in new[] { (program.Workouts[0], 1), (program.Workouts[1], 2), (program.Workouts[1], 3) })
        {
            Assert.True(active.RecordWorkout(program, Finished(program, workout, day), Now.AddDays(day)));
        }

        Assert.Equal((2, ActiveProgramStatus.Active), (active.CurrentCycle, active.Status));

        Assert.True(active.RecordWorkout(program, Finished(program, program.Workouts[0], 4), Now.AddDays(4)));

        Assert.Equal((2, ActiveProgramStatus.Completed, (DateTimeOffset?)Now.AddDays(4)), (active.CurrentCycle, active.Status, active.EndedAtUtc));
        Assert.Empty(active.CurrentCycleCompletions);
        Assert.Equal([1, 1, 2, 2], active.Completions.Select(completion => completion.Cycle));

        // Nothing counts any more.
        Assert.False(active.RecordWorkout(program, Finished(program, program.Workouts[0], 5), Now.AddDays(5)));
    }

    [Fact]
    public void OneCycleOfOneWorkout_CompletesOnTheFirstWorkout()
    {
        var program = Program("Full body");
        var active = ActiveProgram.Activate(program, 1, Now);

        Assert.True(active.RecordWorkout(program, Finished(program, program.Workouts[0], 1), Now.AddDays(1)));

        Assert.Equal(ActiveProgramStatus.Completed, active.Status);
    }

    [Fact]
    public void Completion_KeepsTheSessionAndItsFinishTime()
    {
        var program = Program("Day 1", "Day 2");
        var active = ActiveProgram.Activate(program, 3, Now);
        var session = Finished(program, program.Workouts[1], 1);

        active.RecordWorkout(program, session, Now.AddDays(2));

        var completion = Assert.Single(active.CurrentCycleCompletions);
        Assert.Equal((1, program.Workouts[1].Id, session.Id, session.CompletedAtUtc!.Value, TestUsers.A),
            (completion.Cycle, completion.WorkoutTemplateId, completion.WorkoutSessionId, completion.CompletedAtUtc, completion.UserId));
    }

    [Fact]
    public void SessionsThatAreNotProgressOfThisProgram_DoNotCount()
    {
        var program = Program("Day 1", "Day 2");
        var other = Program("Other");
        var active = ActiveProgram.Activate(program, 3, Now);

        var inProgress = WorkoutSession.Start(program, program.Workouts[0], Now);
        Assert.False(active.RecordWorkout(program, inProgress, Now));

        Assert.False(active.RecordWorkout(program, Finished(other, other.Workouts[0], 1), Now));

        var finished = Finished(program, program.Workouts[0], 1);
        Assert.True(active.RecordWorkout(program, finished, Now));
        Assert.False(active.RecordWorkout(program, finished, Now));

        Assert.Single(active.Completions);
    }

    [Fact]
    public void AWorkoutRemovedFromTheProgram_DoesNotCount()
    {
        var program = Program("Day 1", "Day 2");
        var removed = program.Workouts[1];
        var session = Finished(program, removed, 1);
        var active = ActiveProgram.Activate(program, 3, Now);
        program.RemoveWorkout(removed.Id);

        Assert.False(active.RecordWorkout(program, session, Now));
    }

    [Fact]
    public void RecordWorkout_RefusesAnotherTemplate()
    {
        var program = Program("Day 1");
        var active = ActiveProgram.Activate(program, 3, Now);
        var other = Program("Other");

        Assert.Throws<ArgumentException>(() => active.RecordWorkout(other, Finished(other, other.Workouts[0], 1), Now));
    }

    // ---- Template changes while active ----

    [Fact]
    public void AWorkoutAddedWhileActive_IsRequiredInTheCurrentCycle()
    {
        var program = Program("Day 1", "Day 2");
        var active = ActiveProgram.Activate(program, 3, Now);
        active.RecordWorkout(program, Finished(program, program.Workouts[0], 1), Now);
        AddWorkout(program, "Day 3");

        active.RecordWorkout(program, Finished(program, program.Workouts[1], 2), Now);

        Assert.Equal(1, active.CurrentCycle);
        Assert.True(active.RecordWorkout(program, Finished(program, program.Workouts[2], 3), Now));
        Assert.Equal(2, active.CurrentCycle);
    }

    [Fact]
    public void RemovingTheLastWorkoutToDo_AdvancesTheCycle()
    {
        var program = Program("Day 1", "Day 2", "Day 3");
        var active = ActiveProgram.Activate(program, 3, Now);
        active.RecordWorkout(program, Finished(program, program.Workouts[0], 1), Now);
        active.RecordWorkout(program, Finished(program, program.Workouts[1], 2), Now);

        Assert.False(active.AdvanceIfCycleDone(program, Now));

        program.RemoveWorkout(program.Workouts[2].Id);

        Assert.True(active.AdvanceIfCycleDone(program, Now));
        Assert.Equal(2, active.CurrentCycle);
        Assert.False(active.AdvanceIfCycleDone(program, Now));
    }

    [Fact]
    public void AProgramWithoutWorkouts_NeverAdvances()
    {
        var program = Program("Day 1");
        var active = ActiveProgram.Activate(program, 3, Now);
        program.RemoveWorkout(program.Workouts[0].Id);

        Assert.False(active.AdvanceIfCycleDone(program, Now));
        Assert.Equal((1, ActiveProgramStatus.Active), (active.CurrentCycle, active.Status));
    }

    // ---- Stop ----

    [Fact]
    public void Stop_EndsTheProgram_AndKeepsItsProgress()
    {
        var program = Program("Day 1", "Day 2");
        var active = ActiveProgram.Activate(program, 3, Now);
        active.RecordWorkout(program, Finished(program, program.Workouts[0], 1), Now);

        active.Stop(Now.AddDays(3));

        Assert.Equal((ActiveProgramStatus.Stopped, (DateTimeOffset?)Now.AddDays(3)), (active.Status, active.EndedAtUtc));
        Assert.Single(active.Completions);
        Assert.Empty(active.CurrentCycleCompletions);
        Assert.False(active.RecordWorkout(program, Finished(program, program.Workouts[1], 2), Now));
        Assert.Throws<InvalidOperationException>(() => active.Stop(Now));
    }

    // ---- Helpers ----

    // A program whose workouts all have one Single bench block.
    private WorkoutProgram Program(params string[] workouts)
    {
        var program = WorkoutProgram.Create(TestUsers.A, "Program", Now);

        foreach (var name in workouts)
        {
            AddWorkout(program, name);
        }

        return program;
    }

    private void AddWorkout(WorkoutProgram program, string name) =>
        program.AddWorkout(name).AddBlock(WorkoutBlockKind.Single, 90, [new ExercisePrescription(_bench, null, [new RepRange(8, 8)])]);

    // A session of the workout finished on day `day` after Now.
    private static WorkoutSession Finished(WorkoutProgram program, WorkoutTemplate workout, int day)
    {
        var session = WorkoutSession.Start(program, workout, Now.AddDays(day));
        session.Finish(Now.AddDays(day).AddHours(1));

        return session;
    }
}
