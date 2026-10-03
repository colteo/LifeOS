using System.Runtime.CompilerServices;
using LifeOS.App.Services.Gym;
using LifeOS.Contracts.Gym.Sessions;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.UnitTests.Gym;

// The Home training card (APP-001): which state it shows and which workout it suggests (plain .NET),
// and how the card starts and resumes workouts, which the net10.0 test project cannot render. Those
// checks read the component source.
public class HomeTrainingTests
{
    [Fact]
    public void AWorkoutInProgress_IsResumed_EvenWithAnActiveProgram()
    {
        var program = Active(toDo: [Workout("Day 1", 1)]);

        Assert.Equal(HomeTrainingState.InProgress, HomeTraining.StateOf(Session(program.ProgramId), program));
        Assert.Equal(HomeTrainingState.InProgress, HomeTraining.StateOf(Session(Guid.NewGuid()), null));
    }

    [Fact]
    public void AnActiveProgram_WithoutAWorkoutInProgress_SuggestsTheNextWorkout()
    {
        Assert.Equal(HomeTrainingState.NextWorkout, HomeTraining.StateOf(null, Active(toDo: [Workout("Day 1", 1)])));
    }

    [Fact]
    public void NoActiveProgram_OffersToChooseOne()
    {
        Assert.Equal(HomeTrainingState.NoProgram, HomeTraining.StateOf(null, null));
    }

    [Fact]
    public void NextWorkout_IsTheFirstStillToDo_InProgramOrder()
    {
        // Day 2 was done first (out of order): Day 1 is still the suggestion, then Day 3.
        var program = Active(toDo: [Workout("Day 3", 3), Workout("Day 1", 1), Workout("Day 4", 4)]);

        Assert.Equal("Day 1", HomeTraining.NextWorkout(program)!.Name);
        Assert.Equal("Day 3", HomeTraining.NextWorkout(Active(toDo: [Workout("Day 4", 4), Workout("Day 3", 3)]))!.Name);
    }

    [Fact]
    public void NextWorkout_IsNull_WhenNothingIsLeftInTheCycle()
    {
        Assert.Null(HomeTraining.NextWorkout(Active(toDo: [])));
    }

    [Fact]
    public void InProgressCycle_IsShownOnlyForTheActiveProgramsWorkout()
    {
        var program = Active(toDo: [Workout("Day 1", 1)], currentCycle: 2, totalCycles: 5);

        Assert.Equal("Cycle 2 of 5", HomeTraining.InProgressCycle(Session(program.ProgramId), program));
        Assert.Null(HomeTraining.InProgressCycle(Session(Guid.NewGuid()), program));
        Assert.Null(HomeTraining.InProgressCycle(Session(program.ProgramId), null));
    }

    [Fact]
    public void Card_StartsThroughTheWorkoutStart_AndOpensTheActiveWorkout()
    {
        var card = CardSource();

        Assert.Contains("SessionsApi.GetCurrentAsync()", card);
        Assert.Contains("ActiveProgramApi.GetAsync()", card);
        Assert.Contains("HomeTraining.NextWorkout(active)", card);
        Assert.Contains("StartAsync(active.ProgramId, next.Id)", card);
        Assert.Contains("SessionsApi.StartAsync(programId, workoutId)", card);
        Assert.Contains("Navigation.NavigateTo($\"gym/sessions/{started.Result.Value!.Id}\")", card);
        Assert.Contains("started.InProgressSessionId", card);
        Assert.Contains("Start workout", card);
    }

    [Fact]
    public void Card_ResumesTheWorkoutInProgress_WithoutOfferingAnotherStart()
    {
        var card = CardSource();
        var inProgress = Section(card, "HomeTrainingState.InProgress)", "else if (active is not null)");

        Assert.Contains("href=\"@($\"gym/sessions/{current.Id}\")\"", inProgress);
        Assert.Contains(">Resume<", inProgress);
        Assert.DoesNotContain("Start", inProgress);
        Assert.Contains("if (starting || current is not null)", card);
    }

    [Fact]
    public void Card_WithoutAnActiveProgram_OffersOnlyChoosingOne()
    {
        var card = CardSource();
        var noProgram = card[card.LastIndexOf("No active training program", StringComparison.Ordinal)..];

        Assert.Contains("href=\"gym/programs\"", noProgram);
        Assert.Contains("Choose program", noProgram);
        Assert.DoesNotContain("WorkoutProgramsApiClient", card);
        Assert.DoesNotContain("GetHistoryAsync", card);
    }

    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        var end = source.IndexOf(to, start, StringComparison.Ordinal);

        return source[start..end];
    }

    private static ActiveProgramResponse Active(TrainingWorkoutResponse[] toDo, int currentCycle = 1, int totalCycles = 4) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Program", totalCycles, currentCycle, DateTimeOffset.UnixEpoch, toDo, []);

    private static TrainingWorkoutResponse Workout(string name, int position) =>
        new(Guid.NewGuid(), name, position, 3, 9, true);

    private static WorkoutSessionResponse Session(Guid programId) =>
        new(Guid.NewGuid(), programId, Guid.NewGuid(), "Program", "Day 1", "InProgress", DateTimeOffset.UnixEpoch, null, 2, 9, null, DateTimeOffset.UnixEpoch, []);

    private static string CardSource([CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components", "Gym", "HomeTrainingCard.razor")));
}
