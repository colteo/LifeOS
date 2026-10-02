using LifeOS.App.Services.Gym;
using LifeOS.Contracts.Gym.Programs;

namespace LifeOS.UnitTests.Gym;

// The Gym client's presentation helpers and block editor state (plain .NET files of the MAUI app).
public class GymAppHelpersTests
{
    [Fact]
    public void Sets_UsesTheShortFormWhenEverySetIsTheSame()
    {
        Assert.Equal("3 × 8", GymDisplay.Sets([Set(1, 8, 8), Set(2, 8, 8), Set(3, 8, 8)]));
        Assert.Equal("3 × 8–10", GymDisplay.Sets([Set(1, 8, 10), Set(2, 8, 10), Set(3, 8, 10)]));
        Assert.Equal("12 / 10 / 8", GymDisplay.Sets([Set(1, 12, 12), Set(2, 10, 10), Set(3, 8, 8)]));
        Assert.Equal("10–12 / 8", GymDisplay.Sets([Set(1, 10, 12), Set(2, 8, 8)]));
    }

    [Theory]
    [InlineData(45, "Rest 45 s")]
    [InlineData(60, "Rest 1 min")]
    [InlineData(90, "Rest 1:30 min")]
    [InlineData(120, "Rest 2 min")]
    public void Rest_IsReadable(int seconds, string expected) => Assert.Equal(expected, GymDisplay.Rest(seconds));

    [Fact]
    public void Rest_None() => Assert.Equal("No rest set", GymDisplay.Rest(null));

    [Fact]
    public void SlotLabel_IsAOrBOnlyInASuperset()
    {
        Assert.Equal(("A", "B"), (GymDisplay.SlotLabel(GymDisplay.Superset, 1), GymDisplay.SlotLabel(GymDisplay.Superset, 2)));
        Assert.Null(GymDisplay.SlotLabel(GymDisplay.Single, 1));
    }

    [Fact]
    public void Move_SwapsNeighbours_AndRefusesToLeaveTheList()
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        Assert.Equal([ids[1], ids[0], ids[2]], GymDisplay.Move(ids, 1, -1));
        Assert.Equal([ids[0], ids[2], ids[1]], GymDisplay.Move(ids, 1, 1));
        Assert.Null(GymDisplay.Move(ids, 0, -1));
        Assert.Null(GymDisplay.Move(ids, 2, 1));
    }

    [Fact]
    public void NewDraft_HasOneSlotForSingle_AndABForSuperset_WithThreeByEight()
    {
        var single = BlockDraft.New(GymDisplay.Single);
        var superset = BlockDraft.New(GymDisplay.Superset);

        Assert.Equal([(string?)null], single.Slots.Select(slot => slot.Label));
        Assert.Equal(["A", "B"], superset.Slots.Select(slot => slot.Label));
        Assert.Equal(BlockDraft.DefaultRestSeconds, single.RestSeconds);
        Assert.All(superset.Slots, slot => Assert.Equal([8, 8, 8], slot.Sets.Select(set => set.MinReps!.Value)));
    }

    [Fact]
    public void Fill_IsTheSetsTimesRepsShortcut()
    {
        var slot = ExerciseSlotDraft.New(null);

        slot.Fill(4, 8, 10);

        Assert.Equal([(8, 10), (8, 10), (8, 10), (8, 10)], slot.Sets.Select(set => (set.MinReps!.Value, set.MaxReps!.Value)));
    }

    [Fact]
    public void AddAndRemoveSet_KeepAtLeastOneSet()
    {
        var slot = ExerciseSlotDraft.New(null);
        slot.Fill(1, 12, null);

        slot.AddSet();
        Assert.Equal([12, 12], slot.Sets.Select(set => set.MinReps!.Value));

        slot.RemoveSet(0);
        slot.RemoveSet(0);
        Assert.Single(slot.Sets);
    }

    [Fact]
    public void Validate_ReportsMissingExerciseAndInvalidReps()
    {
        var draft = BlockDraft.New(GymDisplay.Superset);
        draft.Slots[0].ExerciseId = Guid.NewGuid();
        draft.Slots[0].Sets[0].MinReps = 0;
        draft.Slots[0].Sets[1].MinReps = 10;
        draft.Slots[0].Sets[1].MaxReps = 8;

        var errors = draft.Validate();

        Assert.Equal(
            [
                "Set 1 of exercise A: enter reps between 1 and 999.",
                "Set 2 of exercise A: the maximum reps must be at least 10.",
                "Choose exercise B."
            ],
            errors);
    }

    [Fact]
    public void ToExercises_KeepsSlotAndSetOrder_AndAnEmptyMaximumMeansExact()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var draft = BlockDraft.New(GymDisplay.Superset);
        draft.Slots[0].ExerciseId = a;
        draft.Slots[0].Notes = "  Slow  ";
        draft.Slots[0].Fill(2, 8, 10);
        draft.Slots[1].ExerciseId = b;
        draft.Slots[1].Notes = " ";
        draft.Slots[1].Fill(1, 12, null);

        Assert.Empty(draft.Validate());
        var exercises = draft.ToExercises();

        Assert.Equal([(a, "Slow"), (b, (string?)null)], exercises.Select(exercise => (exercise.ExerciseId, exercise.Notes)));
        Assert.Equal([new WorkoutSetRequest(8, 10), new WorkoutSetRequest(8, 10)], exercises[0].Sets);
        Assert.Equal([new WorkoutSetRequest(12, 12)], exercises[1].Sets);
    }

    [Fact]
    public void From_AnExistingBlock_RoundTripsToTheSameRequest()
    {
        var exerciseId = Guid.NewGuid();
        var block = new WorkoutBlockResponse(Guid.NewGuid(), 1, GymDisplay.Single, 120,
            [new WorkoutBlockExerciseResponse(Guid.NewGuid(), 1, exerciseId, "Bench press", "Pause", [Set(1, 12, 12), Set(2, 8, 10)])]);

        var draft = BlockDraft.From(block);

        Assert.Equal((GymDisplay.Single, (int?)120), (draft.Kind, draft.RestSeconds));
        Assert.Null(draft.Slots[0].Sets[0].MaxReps);
        var exercise = Assert.Single(draft.ToExercises());
        Assert.Equal((exerciseId, "Pause"), (exercise.ExerciseId, exercise.Notes));
        Assert.Equal([new WorkoutSetRequest(12, 12), new WorkoutSetRequest(8, 10)], exercise.Sets);
    }

    private static WorkoutSetResponse Set(int position, int min, int max) => new(Guid.NewGuid(), position, min, max);
}
