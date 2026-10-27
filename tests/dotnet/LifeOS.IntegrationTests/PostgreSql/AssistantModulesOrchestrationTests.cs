using LifeOS.Application.Assistant;
using LifeOS.Application.Nutrition.Assistant;
using LifeOS.Application.Nutrition.Hydration.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Nutrition.Assistant;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration.Assistant;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LifeOS.UnitTests.Fakes.Interpretations;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-008: water and meals in the same chat as Finance, end to end on PostgreSQL — turn processor, capability router,
// the three modules, the shared lifecycle, unit of work and real repositories — with a scripted interpreter (no
// model, no network). Every assertion about module data reads the database: nothing is written to Hydration or the
// meal journal before the explicit confirmation, and a confirmation writes exactly one record.
//
// The clock is manual: 2026-10-27 09:41:37Z (10:41 in Rome, CET); users are in Europe/Rome unless stated.
[Collection(PostgreSqlCollection.Name)]
public class AssistantModulesOrchestrationTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 41, 37, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 27);

    private readonly ManualTimeProvider _clock = new(Now);

    // ---- Water ----

    [Fact]
    public async Task Water_IsPreparedCorrectedAndRecordedOnlyByTheConfirmation_Once()
    {
        var user = await NewUserAsync();
        var conversation = await NewConversationAsync(user);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Drink(Water(Stated("300", "300 ml"), Stated("ml"))))
            .Then(UpdateWater(Water(Stated("0.5", "mezzo litro"), Stated("l", "litro"))));

        AssertCompleted(await SendAsync(user, conversation, "Ho bevuto 300 ml d'acqua", interpreter), "Ho preparato 300 ml d'acqua. Vuoi modificarla?");
        AssertCompleted(await SendAsync(user, conversation, "no, mezzo litro", interpreter), "Ho preparato 500 ml d'acqua. Vuoi modificarla?");

        var draft = (await WaterAsync(user, conversation))!;
        Assert.Equal((AssistantOperationStatus.AwaitingConfirmation, 2, 500, true),
            (draft.Operation.Status, draft.Operation.DraftVersion, draft.Operation.Draft.AmountMl, draft.CanConfirm));
        Assert.Equal(0, await IntakeCountAsync(user));

        // The interpreter received the open water draft's field names only, and no Finance categories.
        var second = interpreter.Requests[1].Context;
        Assert.Equal(("hydration.intake.create", (string?)null), (second.OpenDraft!.Kind, second.OpenDraft.Type));
        Assert.Equal(["amount", "unit"], second.OpenDraft.FilledFields);
        Assert.Equal(AssistantFinanceContext.None, second.Finance);

        var confirmed = await ConfirmWaterAsync(user, draft.Operation.Id, 2);
        var replayed = await ConfirmWaterAsync(user, draft.Operation.Id, 2);

        Assert.Equal((AssistantResultStatus.Ok, true), (confirmed.Status, confirmed.Value!.Changed));
        Assert.Equal((AssistantResultStatus.Ok, false), (replayed.Status, replayed.Value!.Changed));
        var resultId = confirmed.Value.View.Operation.ResultId!.Value;
        Assert.Equal(resultId, replayed.Value.View.Operation.ResultId);

        var intake = Assert.Single(await IntakesAsync(user));
        Assert.Equal((resultId, 500, Today, new TimeOnly(10, 41)), (intake.Id, intake.AmountMl, intake.DiaryDate, intake.DiaryTime));
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 9, 41, 0, TimeSpan.Zero), intake.OccurredAtUtc);
        Assert.Null(await WaterAsync(user, conversation));
    }

    [Fact]
    public async Task AGlassIsNotAQuantity_TheAmountIsAsked_AndTheDraftCannotBeConfirmed()
    {
        var user = await NewUserAsync();
        var conversation = await NewConversationAsync(user);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Drink(Water()))
            .Then(UpdateWater(Water(Stated("300"))))
            .Then(UpdateWater(Water(Stated("250", "250 ml"), Stated("ml"))));

        AssertCompleted(await SendAsync(user, conversation, "Ho bevuto un bicchiere d'acqua", interpreter), HydrationAssistantReplies.AmountQuestion);
        var draft = (await WaterAsync(user, conversation))!;
        Assert.Equal(AssistantOperationStatus.Collecting, draft.Operation.Status);
        Assert.Equal([HydrationDraftFields.AmountMl], draft.MissingFields);

        var refused = await ConfirmWaterAsync(user, draft.Operation.Id, draft.Operation.DraftVersion);
        Assert.Equal((AssistantResultStatus.Rejected, AssistantErrors.DraftIncomplete), (refused.Status, refused.Code));

        // A number without its unit is asked again, never assumed to be millilitres.
        AssertCompleted(await SendAsync(user, conversation, "300", interpreter), "In che unità? Indica ml, cl o litri.");
        Assert.Null((await WaterAsync(user, conversation))!.Operation.Draft.AmountMl);

        AssertCompleted(await SendAsync(user, conversation, "250 ml", interpreter), "Ho preparato 250 ml d'acqua. Vuoi modificarla?");
        Assert.Equal(0, await IntakeCountAsync(user));
    }

    [Fact]
    public async Task WaterOnAPastDay_IsRecordedOnThatDayAtNoonWithThatDaysOffset_AndAFutureDayIsRefused()
    {
        var user = await NewUserAsync("America/New_York");
        var conversation = await NewConversationAsync(user);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Drink(Water(Stated("1", "1 litro"), Stated("l", "litro"), Date(AssistantDateKind.Calendar, "28/10", day: 28, month: 10))))
            .Then(UpdateWater(Water(date: Date(AssistantDateKind.Yesterday, "ieri"))));

        AssertCompleted(await SendAsync(user, conversation, "1 litro il 28/10", interpreter),
            "Non posso registrare acqua per un giorno futuro. Quando l'hai bevuta?");
        var future = (await WaterAsync(user, conversation))!;
        Assert.Equal([HydrationOperationErrors.FutureDate], future.Issues);
        Assert.False(future.CanConfirm);
        Assert.Equal((AssistantResultStatus.Rejected, HydrationOperationErrors.FutureDate),
            await ConfirmWaterAsync(user, future.Operation.Id, future.Operation.DraftVersion) is var refused ? (refused.Status, refused.Code) : default);

        AssertCompleted(await SendAsync(user, conversation, "ieri", interpreter), "Ho preparato 1000 ml d'acqua. Vuoi modificarla?");
        var past = (await WaterAsync(user, conversation))!;
        var occurrence = past.Operation.Draft.Occurrence!;

        // 05:41 EDT on the 27th: yesterday is the 26th, at the 12:00 convention, offset −04:00.
        Assert.Equal((new DateOnly(2026, 10, 26), new TimeOnly(12, 0), AssistantDraftTimeSource.Noon, -240),
            (occurrence.LocalDate, occurrence.LocalTime, occurrence.TimeSource, occurrence.UtcOffsetMinutes));

        Assert.Equal(AssistantResultStatus.Ok, (await ConfirmWaterAsync(user, past.Operation.Id, past.Operation.DraftVersion)).Status);
        var intake = Assert.Single(await IntakesAsync(user));
        Assert.Equal((new DateOnly(2026, 10, 26), new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.Zero)), (intake.DiaryDate, intake.OccurredAtUtc));
    }

    // ---- Meals ----

    [Fact]
    public async Task AMealWithItsDescription_IsReadyAtOnce_AndIsRecordedAsTextOnly()
    {
        var user = await NewUserAsync();
        var conversation = await NewConversationAsync(user);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Meal(MealFields(Stated("pasta al pomodoro"), Stated("lunch", "pranzo"), time: Time(13, 15, "alle 13:15"))))
            .Then(UpdateMeal(MealFields(mealType: Stated("dinner", "cena"))));

        AssertCompleted(await SendAsync(user, conversation, "A pranzo alle 13:15 ho mangiato pasta al pomodoro", interpreter),
            "Ho preparato il pasto. Vuoi modificarlo? L'ora è nel futuro: controllala prima di confermare.");
        AssertCompleted(await SendAsync(user, conversation, "era una cena", interpreter),
            "Ho preparato il pasto. Vuoi modificarlo? L'ora è nel futuro: controllala prima di confermare.");

        var draft = (await MealAsync(user, conversation))!;
        Assert.Equal(("pasta al pomodoro", MealType.Dinner, new TimeOnly(13, 15), true),
            (draft.Operation.Draft.Description, draft.Operation.Draft.MealType, draft.Operation.Draft.Occurrence!.LocalTime, draft.CanConfirm));
        Assert.Equal(0, await MealCountAsync(user));

        var confirmed = await ConfirmMealAsync(user, draft.Operation.Id, draft.Operation.DraftVersion);

        Assert.Equal(AssistantResultStatus.Ok, confirmed.Status);
        var meal = Assert.Single(await MealsAsync(user));
        Assert.Equal((confirmed.Value!.View.Operation.ResultId, "pasta al pomodoro", MealType.Dinner, Today, new TimeOnly(13, 15)),
            ((Guid?)meal.Id, meal.Description, meal.MealType, meal.DiaryDate, meal.DiaryTime));
        Assert.Equal(0, await NutritionCountAsync(meal.Id));
    }

    [Fact]
    public async Task AMealWithoutDescription_AsksWhatWasEaten_AndACancelledMealWritesNothing()
    {
        var user = await NewUserAsync();
        var conversation = await NewConversationAsync(user);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Meal(MealFields(mealType: Stated("lunch", "pranzato"))))
            .Then(Of(AssistantIntent.DraftCancel));

        AssertCompleted(await SendAsync(user, conversation, "Ho pranzato", interpreter), MealAssistantReplies.DescriptionQuestion);
        AssertCompleted(await SendAsync(user, conversation, "annulla", interpreter), MealAssistantReplies.CancelWithButton);

        var draft = (await MealAsync(user, conversation))!;
        var cancelled = await CancelMealAsync(user, draft.Operation.Id, draft.Operation.DraftVersion);

        Assert.Equal(AssistantOperationStatus.Cancelled, cancelled.Value!.View.Operation.Status);
        Assert.Null(await MealAsync(user, conversation));
        Assert.Equal(0, await MealCountAsync(user));
    }

    // ---- One chat, one open draft ----

    [Fact]
    public async Task WhileADraftIsOpen_ANewRecordOfAnotherModuleIsNotStarted_AndTheDraftIsUntouched()
    {
        var user = await NewUserAsync();
        await NewAccountAsync(user);
        var conversation = await NewConversationAsync(user);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("5", "5 euro"))))
            .Then(Drink(Water(Stated("300", "300 ml"), Stated("ml"))))
            .Then(Meal(MealFields(Stated("pizza"))));

        AssertCompleted(await SendAsync(user, conversation, "Speso 5 euro", interpreter), "Da quale conto?");
        AssertCompleted(await SendAsync(user, conversation, "bevuto 300 ml", interpreter), AssistantCoreReplies.OtherDraftOpen("transazione"));
        AssertCompleted(await SendAsync(user, conversation, "pizza", interpreter), AssistantCoreReplies.OtherDraftOpen("transazione"));

        Assert.Equal(1, await OperationCountAsync(conversation));
        Assert.Null(await WaterAsync(user, conversation));
        Assert.Equal((0, 0), (await IntakeCountAsync(user), await MealCountAsync(user)));
    }

    [Fact]
    public async Task AnOpenWaterDraft_TakesTheDraftIntents_AndFinanceDoesNotSeeIt()
    {
        var user = await NewUserAsync();
        var conversation = await NewConversationAsync(user);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Drink(Water(Stated("300", "300 ml"), Stated("ml"))))
            .Then(Of(AssistantIntent.DraftConfirm))
            .Then(Expense(Fields(amount: Stated("5", "5 euro"))));

        await SendAsync(user, conversation, "bevuto 300 ml", interpreter);

        // "Confermo" in chat never confirms: the reply points to the button and nothing is written.
        AssertCompleted(await SendAsync(user, conversation, "confermo", interpreter), HydrationAssistantReplies.ConfirmWithButton);
        AssertCompleted(await SendAsync(user, conversation, "speso 5 euro", interpreter), AssistantCoreReplies.OtherDraftOpen("acqua"));
        Assert.Equal((0, 1), (await IntakeCountAsync(user), await OperationCountAsync(conversation)));
    }

    [Fact]
    public async Task ADraftSurvivesReopening_AndThePersistedDraftIsTheState()
    {
        var user = await NewUserAsync();
        var conversation = await NewConversationAsync(user);
        await SendAsync(user, conversation, "ho mangiato una pizza", new ScriptedAssistantInterpreter().Then(Meal(MealFields(Stated("una pizza")))));

        // A new request scope, as after closing and reopening the app.
        var reopened = (await MealAsync(user, conversation))!;
        var header = await ReaderAsync(user, conversation);

        Assert.Equal(("una pizza", 1, AssistantOperationKinds.NutritionMealCreate), (reopened.Operation.Draft.Description, reopened.Operation.DraftVersion,
            header!.Kind));
    }

    [Fact]
    public async Task ABrokenInterpretation_FailsTheTurn_AndOpensNothing()
    {
        var user = await NewUserAsync();
        var conversation = await NewConversationAsync(user);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Drink(Water(Stated("250", "250 ml"), Stated("ml"))))                         // a quantity the message does not contain
            .Then(Meal(MealFields(Stated("pizza con mozzarella e basilico", "pizza"))))          // ingredients the user did not write
            .Then(Of(AssistantIntent.FinanceExpense, Fields(amount: Stated("5", "5 euro")), hydration: Water(Stated("300", "300 ml"), Stated("ml"))));

        AssertFailed(await SendAsync(user, conversation, "Ho bevuto un bicchiere d'acqua", interpreter), AssistantErrors.AssistantInvalidOutput);
        AssertFailed(await SendAsync(user, conversation, "Ho mangiato una pizza", interpreter), AssistantErrors.AssistantInvalidOutput);
        AssertFailed(await SendAsync(user, conversation, "Speso 5 euro e bevuto 300 ml", interpreter), AssistantErrors.AssistantInvalidOutput);

        Assert.Equal(0, await OperationCountAsync(conversation));
    }

    [Fact]
    public async Task AModuleThatIsNotEnabled_AnswersNotAvailable()
    {
        var user = await NewUserAsync();
        var conversation = await NewConversationAsync(user);

        await using var scope = fixture.CreateScope();
        var turn = await AssistantProcessorComposition.Processor(scope.ServiceProvider, _clock,
                new ScriptedAssistantInterpreter().Then(Drink(Water(Stated("300", "300 ml"), Stated("ml")))))
            .SendAsync(user.Id, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), "bevuto 300 ml"), default);

        AssertCompleted(turn, AssistantProcessorComposition.FinanceOnlyReplies.NotAvailable);
        Assert.Equal(0, await OperationCountAsync(conversation));
    }

    // ---- Helpers ----

    private static void AssertCompleted(AssistantResult<AssistantTurn> turn, string reply)
    {
        Assert.Equal(AssistantResultStatus.Ok, turn.Status);
        Assert.Equal((AssistantTurnStatus.Completed, reply), (turn.Value!.UserMessage.TurnStatus!.Value, turn.Value.Reply?.Content));
    }

    private static void AssertFailed(AssistantResult<AssistantTurn> turn, string code)
    {
        Assert.Equal(AssistantResultStatus.Ok, turn.Status);
        Assert.Equal((AssistantTurnStatus.Failed, code), (turn.Value!.UserMessage.TurnStatus!.Value, turn.Value.UserMessage.FailureCode));
    }

    private async Task<AssistantResult<AssistantTurn>> SendAsync(User user, Guid conversation, string text, IAssistantInterpreter interpreter)
    {
        await using var scope = fixture.CreateScope();
        return await AssistantProcessorComposition.Processor(scope.ServiceProvider, _clock, interpreter,
                modules: AssistantProcessorComposition.AllModules(scope.ServiceProvider, _clock))
            .SendAsync(user.Id, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), text), default);
    }

    private async Task<AssistantOperationView<HydrationIntakeDraft>?> WaterAsync(User user, Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        return (await AssistantProcessorComposition.HydrationOperations(scope.ServiceProvider, _clock).GetActiveAsync(user.Id, conversation, default)).Value;
    }

    private async Task<AssistantOperationView<MealDraft>?> MealAsync(User user, Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        return (await AssistantProcessorComposition.MealOperations(scope.ServiceProvider, _clock).GetActiveAsync(user.Id, conversation, default)).Value;
    }

    private async Task<AssistantResult<AssistantOperationOutcome<HydrationIntakeDraft>>> ConfirmWaterAsync(User user, Guid operation, int version)
    {
        await using var scope = fixture.CreateScope();
        return await AssistantProcessorComposition.HydrationOperations(scope.ServiceProvider, _clock).Lifecycle.ConfirmAsync(user.Id, operation, version, default);
    }

    private async Task<AssistantResult<AssistantOperationOutcome<MealDraft>>> ConfirmMealAsync(User user, Guid operation, int version)
    {
        await using var scope = fixture.CreateScope();
        return await AssistantProcessorComposition.MealOperations(scope.ServiceProvider, _clock).Lifecycle.ConfirmAsync(user.Id, operation, version, default);
    }

    private async Task<AssistantResult<AssistantOperationOutcome<MealDraft>>> CancelMealAsync(User user, Guid operation, int version)
    {
        await using var scope = fixture.CreateScope();
        return await AssistantProcessorComposition.MealOperations(scope.ServiceProvider, _clock).Lifecycle.CancelAsync(user.Id, operation, version, default);
    }

    private async Task<AssistantOperationHeader?> ReaderAsync(User user, Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAssistantOperationReader>().GetOpenAsync(user.Id, conversation, default);
    }

    private async Task<List<HydrationIntake>> IntakesAsync(User user)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().HydrationIntakes.AsNoTracking()
            .Where(intake => intake.UserId == user.Id).ToListAsync();
    }

    private async Task<int> IntakeCountAsync(User user) => (await IntakesAsync(user)).Count;

    private async Task<List<MealEntry>> MealsAsync(User user)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().MealEntries.AsNoTracking()
            .Where(meal => meal.UserId == user.Id).ToListAsync();
    }

    private async Task<int> MealCountAsync(User user) => (await MealsAsync(user)).Count;

    private async Task<int> NutritionCountAsync(Guid mealId)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM meal_nutrition_snapshots WHERE meal_entry_id = {mealId}").SingleAsync();
    }

    private async Task<int> OperationCountAsync(Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM assistant_operations WHERE conversation_id = {conversation}").SingleAsync();
    }

    private async Task<User> NewUserAsync(string timeZone = "Europe/Rome")
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));
        user.SetTimeZone(timeZone);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task NewAccountAsync(User user) =>
        await PostgresAssert.InsertAsync(fixture, Account.Create(user.Id, "Revolut", AccountType.BankAccount, "EUR", Now.AddDays(-1)));

    private async Task<Guid> NewConversationAsync(User user)
    {
        var conversation = AssistantConversation.Create(user.Id, Guid.CreateVersion7(), Now);
        await PostgresAssert.InsertAsync(fixture, conversation);

        return conversation.Id;
    }
}
