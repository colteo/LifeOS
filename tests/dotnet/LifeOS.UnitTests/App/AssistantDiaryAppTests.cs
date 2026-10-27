using System.Globalization;
using LifeOS.App.Services.Assistant;
using LifeOS.Contracts.Assistant;

namespace LifeOS.UnitTests.App;

// AI-008: the app side of water and meal drafts — the edit form model (only the draft's own fields are ever sent)
// and the texts by kind (title, one-line summary, recorded notice). Finance's draft editor is covered in
// AssistantAppTests.
public class AssistantDiaryAppTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AWaterEdit_SendsOnlyTheChangedMillilitres()
    {
        var editor = AssistantDiaryDraftEditor.From(Water(300));
        editor.AmountMlText = "500";

        var (request, error) = editor.BuildPatch(4);

        Assert.Null(error);
        Assert.Equal((4, 500), (request!.ExpectedVersion, request.AmountMl));
        Assert.Equal((null, null, null, null, null, null), (request.Type, request.Amount, request.AccountId, request.CategoryId, request.Note, request.Description));
        Assert.Null(request.MealType);
        Assert.Null(request.Clear);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4001")]
    [InlineData("1,5")]
    [InlineData("mezzo litro")]
    public void AWaterEdit_RefusesAnythingButWholeMillilitres(string text)
    {
        var editor = AssistantDiaryDraftEditor.From(Water(300));
        editor.AmountMlText = text;

        Assert.Equal((null, AssistantDiaryDraftEditor.AmountMlInvalidMessage), editor.BuildPatch(4));
    }

    [Fact]
    public void AnUntouchedEdit_SendsNothing_AndAnEmptiedFieldIsCleared()
    {
        var unchanged = AssistantDiaryDraftEditor.From(Water(300));
        var emptied = AssistantDiaryDraftEditor.From(Water(300));
        emptied.AmountMlText = "";

        Assert.Equal((null, null), unchanged.BuildPatch(4));
        Assert.Equal(["amountMl"], emptied.BuildPatch(4).Request!.Clear!);
    }

    [Fact]
    public void AMealEdit_SendsTheDescriptionTypeAndTime_AndClearsAnEmptiedType()
    {
        var editor = AssistantDiaryDraftEditor.From(Meal("pasta", "Lunch"));
        editor.Description = "  pasta al pomodoro ";
        editor.MealType = "";
        editor.OccurredTime = "13:15";

        var (request, error) = editor.BuildPatch(2);

        Assert.Null(error);
        Assert.Equal(("pasta al pomodoro", "13:15", (int?)null), (request!.Description, request.OccurredTime, request.AmountMl));
        Assert.Equal(["mealType"], request.Clear!);
    }

    [Fact]
    public void TheEditor_IsOnlyForWaterAndMealDrafts() =>
        Assert.Throws<ArgumentException>(() => AssistantDiaryDraftEditor.From(Water(300) with { Kind = AssistantOperationKindNames.FinanceTransactionCreate }));

    [Fact]
    public void Texts_FollowTheKind()
    {
        var culture = CultureInfo.InvariantCulture;

        Assert.Equal(("Water", "Water · 300 ml", "Water recorded."),
            (AssistantDisplay.TitleText(Water(300)), AssistantDisplay.SummaryText(Water(300), culture), AssistantDisplay.RecordedNotice(Water(300).Kind)));
        Assert.Equal(("Meal", "Meal · pasta", "Meal recorded."),
            (AssistantDisplay.TitleText(Meal("pasta", null)), AssistantDisplay.SummaryText(Meal("pasta", null), culture),
                AssistantDisplay.RecordedNotice(AssistantOperationKindNames.NutritionMealCreate)));
        Assert.Equal("Water · —", AssistantDisplay.SummaryText(Water(null), culture));
        Assert.Equal(AssistantChatFlow.RecordedNotice, AssistantDisplay.RecordedNotice(AssistantOperationKindNames.FinanceTransactionCreate));
        Assert.Equal("Missing: amount, date.", AssistantDisplay.MissingText(["amountMl", "occurredAt"]));
        Assert.Equal("Missing: what you ate.", AssistantDisplay.MissingText(["description"]));
        Assert.False(AssistantDisplay.IsFinance(Water(300)));
    }

    private static AssistantOperationResponse Water(int? amountMl) =>
        Operation(AssistantOperationKindNames.HydrationIntakeCreate, hydration: new HydrationIntakeDraftResponse(amountMl, null, null, null));

    private static AssistantOperationResponse Meal(string? description, string? mealType) =>
        Operation(AssistantOperationKindNames.NutritionMealCreate, meal: new MealDraftResponse(description, mealType, null, null, null));

    private static AssistantOperationResponse Operation(string kind, HydrationIntakeDraftResponse? hydration = null, MealDraftResponse? meal = null) =>
        new(Guid.CreateVersion7(), Guid.Empty, kind, AssistantOperationStatuses.AwaitingConfirmation, 4, 1, null, [], [], true, Now.AddDays(1), null,
            Now, Now, null, hydration, meal);
}
