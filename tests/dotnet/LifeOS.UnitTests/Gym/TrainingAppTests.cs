using System.Runtime.CompilerServices;
using LifeOS.App.Services.Gym;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.UnitTests.Gym;

// The Train entry point of the Gym client: its presentation helper (plain .NET), and the separation of
// training from authoring in the Razor pages, which the net10.0 test project cannot render. Those
// checks read the page sources.
public class TrainingAppTests
{
    [Fact]
    public void Availability_TellsNoProgramsFromNoWorkouts()
    {
        Assert.Equal(TrainingDisplay.Availability.NoPrograms, TrainingDisplay.Of([]));
        Assert.Equal(TrainingDisplay.Availability.NoWorkouts, TrainingDisplay.Of([Program("A"), Program("B")]));
        Assert.Equal(TrainingDisplay.Availability.Workouts, TrainingDisplay.Of([Program("A"), Program("B", Workout("Push", canStart: false))]));
    }

    [Fact]
    public void Choices_OmitProgramsWithoutWorkouts_AndKeepTheirOrder()
    {
        var choices = TrainingDisplay.Choices([Program("Hypertrophy", Workout("Push"), Workout("Pull")), Program("Empty"), Program("Strength", Workout("Upper A"))]);

        Assert.Equal(["Hypertrophy", "Strength"], choices.Select(program => program.Name));
        Assert.Equal(["Push", "Pull"], choices[0].Workouts.Select(workout => workout.Name));
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

    private static TrainingProgramResponse Program(string name, params TrainingWorkoutResponse[] workouts) =>
        new(Guid.NewGuid(), name, workouts);

    private static TrainingWorkoutResponse Workout(string name, bool canStart = true, int exercises = 2, int sets = 6) =>
        canStart
            ? new(Guid.NewGuid(), name, 1, exercises, sets, true)
            : new(Guid.NewGuid(), name, 1, 0, 0, false);

    private static string PageSource(string fileName, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components", "Pages", "Gym", fileName)));
}
