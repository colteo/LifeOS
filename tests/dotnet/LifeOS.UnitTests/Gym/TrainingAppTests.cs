using System.Runtime.CompilerServices;
using LifeOS.App.Services.Gym;
using LifeOS.Contracts.Gym.Programs;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.UnitTests.Gym;

// The Train entry point of the Gym client (the active program, GYM-004): its presentation helper (plain
// .NET), and the separation of training from authoring in the Razor pages, which the net10.0 test
// project cannot render. Those checks read the page sources.
public class TrainingAppTests
{
    [Fact]
    public void Cycle_ShowsTheCurrentCycleOfTheTotal_AndWhatIsDoneInIt()
    {
        var active = Active(currentCycle: 2, totalCycles: 5, toDo: [Workout("Day 1"), Workout("Day 3")], done: ["Day 2"]);

        Assert.Equal("Cycle 2 of 5", TrainingDisplay.Cycle(active));
        Assert.Equal("1 of 3 workouts done", TrainingDisplay.CycleProgress(active));
        Assert.Equal("0 of 1 workout done", TrainingDisplay.CycleProgress(Active(1, 1, [Workout("Full body")], [])));
    }

    [Fact]
    public void ActivationBlocker_RequiresWorkoutsWithExercises()
    {
        Assert.Equal("Add a workout before activating the program.", TrainingDisplay.ActivationBlocker(ProgramWith()));
        Assert.Equal("Every workout needs exercises before activating the program.", TrainingDisplay.ActivationBlocker(ProgramWith(1, 0)));
        Assert.Null(TrainingDisplay.ActivationBlocker(ProgramWith(1, 2)));
    }

    [Fact]
    public void Cycles_MustBeBetweenOneAndTheMaximum()
    {
        Assert.False(TrainingDisplay.IsValidCycles(0));
        Assert.True(TrainingDisplay.IsValidCycles(1));
        Assert.True(TrainingDisplay.IsValidCycles(TrainingDisplay.DefaultCycles));
        Assert.True(TrainingDisplay.IsValidCycles(99));
        Assert.False(TrainingDisplay.IsValidCycles(100));
    }

    [Fact]
    public void Summary_CountsExercisesAndSets_OrSaysWhyItCannotStart()
    {
        Assert.Equal("5 exercises · 15 sets", TrainingDisplay.Summary(Workout("Push", exercises: 5, sets: 15)));
        Assert.Equal("1 exercise · 1 set", TrainingDisplay.Summary(Workout("Test", exercises: 1, sets: 1)));
        Assert.Equal("No exercises yet", TrainingDisplay.Summary(Workout("Empty", canStart: false)));
    }

    [Fact]
    public void CanStartNow_OnlyAReadyWorkout_AndNotWhileAnotherIsInProgress()
    {
        Assert.True(TrainingDisplay.CanStartNow(Workout("Push"), workoutInProgress: false));
        Assert.False(TrainingDisplay.CanStartNow(Workout("Push"), workoutInProgress: true));
        Assert.False(TrainingDisplay.CanStartNow(Workout("Empty", canStart: false), workoutInProgress: false));
    }

    [Fact]
    public void GymHub_OffersTrainAndProgramsSeparately_AndResume()
    {
        var hub = PageSource("GymHub.razor");

        Assert.Contains("href=\"gym/train\"", hub);
        Assert.Contains("href=\"gym/programs\"", hub);
        Assert.Contains("<WorkoutInProgressCard Session=\"current\" />", hub);
        Assert.Contains("SessionsApi.GetCurrentAsync()", hub);
    }

    [Fact]
    public void Train_StartsThroughTheWorkoutStart_OffersResume_AndHasNoAuthoring()
    {
        var train = PageSource("Train.razor");

        Assert.Contains("@page \"/gym/train\"", train);
        Assert.Contains("ActiveProgramApi.GetAsync()", train);
        Assert.Contains("StartAsync(active.ProgramId, workout.Id)", train);
        Assert.Contains("href=\"gym/programs\"", train);
        Assert.Contains("SessionsApi.StartAsync(programId, workoutId)", train);
        Assert.Contains("Navigation.NavigateTo($\"gym/sessions/{started.Result.Value!.Id}\")", train);
        Assert.Contains("<WorkoutInProgressCard Session=\"current\" />", train);
        Assert.DoesNotContain("WorkoutProgramsApiClient", train);
        Assert.DoesNotContain("ExercisesApiClient", train);
    }

    [Fact]
    public void WorkoutDetail_IsAuthoringOnly_AndKeepsItsEditing()
    {
        var detail = PageSource("WorkoutDetail.razor");

        Assert.DoesNotContain("WorkoutSessionsApiClient", detail);
        Assert.DoesNotContain("Start workout", detail);
        Assert.DoesNotContain("gym/sessions", detail);
        Assert.Contains("ProgramsApi.ReorderBlocksAsync", detail);
        Assert.Contains("ProgramsApi.DeleteBlockAsync", detail);
        Assert.Contains("NewBlockHref(GymDisplay.Single)", detail);
        Assert.Contains("NewBlockHref(GymDisplay.Superset)", detail);
    }

    [Fact]
    public void ProgramDetail_ActivatesThroughTheActiveProgramApi()
    {
        var detail = PageSource("ProgramDetail.razor");

        Assert.Contains("ActiveProgramApi.ActivateAsync(ProgramId, cycles)", detail);
        Assert.Contains("TrainingDisplay.ActivationBlocker(program)", detail);
        Assert.DoesNotContain("WorkoutSessionsApiClient", detail);
    }

    private static ActiveProgramResponse Active(int currentCycle, int totalCycles, TrainingWorkoutResponse[] toDo, string[] done) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Program", totalCycles, currentCycle, DateTimeOffset.UnixEpoch, toDo,
            done.Select((name, index) => new CompletedCycleWorkoutResponse(Guid.NewGuid(), name, index + 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch)).ToList());

    // A program whose workouts have the given numbers of blocks.
    private static WorkoutProgramResponse ProgramWith(params int[] blockCounts) =>
        new(Guid.NewGuid(), "Program", DateTimeOffset.UnixEpoch, blockCounts
            .Select((blocks, index) => new WorkoutResponse(Guid.NewGuid(), $"Day {index + 1}", index + 1,
                Enumerable.Range(1, blocks).Select(position => new WorkoutBlockResponse(Guid.NewGuid(), position, "Single", null, [])).ToList()))
            .ToList());

    private static TrainingWorkoutResponse Workout(string name, bool canStart = true, int exercises = 2, int sets = 6) =>
        canStart
            ? new(Guid.NewGuid(), name, 1, exercises, sets, true)
            : new(Guid.NewGuid(), name, 1, 0, 0, false);

    private static string PageSource(string fileName, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components", "Pages", "Gym", fileName)));
}
