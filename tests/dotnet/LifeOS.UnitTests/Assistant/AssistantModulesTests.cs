using LifeOS.Application.Assistant;
using LifeOS.Application.Nutrition.Assistant;
using LifeOS.Application.Nutrition.Hydration.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Nutrition.Assistant;
using LifeOS.Domain.Nutrition.Hydration.Assistant;
using static LifeOS.UnitTests.Fakes.Interpretations;

namespace LifeOS.UnitTests.Assistant;

// AI-008: the multi-module pieces that need no database — the water quantity conversion, the water and meal drafts,
// the diary date/time and UTC offset rules, the protocol v2 validator rules and the router's routing table.
public class AssistantModulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 41, 37, TimeSpan.Zero);

    // ---- Water quantities: ml, cl and litres, never a guess ----

    [Theory]
    [InlineData("300", "ml", 300)]
    [InlineData("25", "cl", 250)]
    [InlineData("0.5", "l", 500)]
    [InlineData("1.5", "l", 1500)]
    [InlineData("4", "l", 4000)]
    [InlineData("1", "ml", 1)]
    public void AStatedQuantity_BecomesWholeMillilitres(string amount, string unit, int expected) =>
        Assert.Equal(expected, HydrationQuantity.ToMillilitres(amount, unit));

    [Theory]
    [InlineData("0.3333", "l")]   // not whole millilitres: refused, not rounded
    [InlineData("4.5", "l")]      // above the intake's limit
    [InlineData("0", "ml")]
    [InlineData("300", "glass")]  // a container is not a unit
    [InlineData("300", null)]
    [InlineData("1,5", "l")]      // the protocol's decimal uses a dot
    [InlineData("", "ml")]
    public void AnythingElse_IsNoQuantity(string? amount, string? unit) => Assert.Null(HydrationQuantity.ToMillilitres(amount, unit));

    // ---- Drafts ----

    [Fact]
    public void AWaterDraft_IsCompleteWithAnAmountAndADate_AndHoldsOnlyValidMillilitres()
    {
        var occurrence = AssistantDiaryOccurrenceResolver.Resolve(null, null, "Europe/Rome", Now);

        Assert.Equal([HydrationDraftFields.AmountMl, HydrationDraftFields.OccurredAt], HydrationIntakeDraft.Empty.MissingFields);
        Assert.True(HydrationIntakeDraft.Create(300, null, null, occurrence).IsComplete);
        Assert.Equal(HydrationDraftFields.AmountMl, Assert.ThrowsAny<ArgumentException>(() => HydrationIntakeDraft.Create(4001, null, null, null)).ParamName);
        Assert.Equal(HydrationDraftFields.AmountMl, Assert.ThrowsAny<ArgumentException>(() => HydrationIntakeDraft.Create(0, null, null, null)).ParamName);
        Assert.Equal(HydrationDraftFields.OccurredTime,
            Assert.ThrowsAny<ArgumentException>(() => HydrationIntakeDraft.Create(300, null, new TimeOnly(8, 30, 15), null)).ParamName);
    }

    [Fact]
    public void AMealDraft_NeedsOnlyADescription_TrimsIt_AndRefusesNul()
    {
        var occurrence = AssistantDiaryOccurrenceResolver.Resolve(null, null, "Europe/Rome", Now);
        var draft = MealDraft.Create("  pasta al pomodoro  ", null, null, null, occurrence);

        Assert.Equal(("pasta al pomodoro", true), (draft.Description, draft.IsComplete));
        Assert.Equal([MealDraftFields.Description, MealDraftFields.OccurredAt], MealDraft.Empty.MissingFields);
        Assert.Null(MealDraft.Create("   ", MealType.Lunch, null, null, null).Description);
        Assert.Equal(MealDraftFields.Description, Assert.ThrowsAny<ArgumentException>(() => MealDraft.Create("pasta\0", null, null, null, null)).ParamName);
        Assert.Equal(MealDraftFields.Description,
            Assert.ThrowsAny<ArgumentException>(() => MealDraft.Create(new string('x', MealDraft.MaxDescriptionLength + 1), null, null, null, null)).ParamName);
    }

    [Fact]
    public void ARevisionEqualToTheDraft_ChangesNothing()
    {
        var occurrence = AssistantDiaryOccurrenceResolver.Resolve(null, null, "Europe/Rome", Now);
        var draft = HydrationIntakeDraft.Create(300, null, null, occurrence);

        Assert.Equal(draft, HydrationDraftBuilder.Apply(draft, new HydrationDraftPatch(AmountMl: 300), "Europe/Rome", Now.AddMinutes(5)));
        Assert.NotEqual(draft, HydrationDraftBuilder.Apply(draft, new HydrationDraftPatch(AmountMl: 500), "Europe/Rome", Now));
    }

    // ---- Dates, times and offsets (whole minutes, the user's zone, never assumed) ----

    [Fact]
    public void Today_IsNowToTheMinute_InTheUsersZone_WithItsOffset()
    {
        var occurrence = AssistantDiaryOccurrenceResolver.Resolve(null, null, "Europe/Rome", Now)!;

        Assert.Equal((new DateOnly(2026, 10, 27), new TimeOnly(10, 41), AssistantDraftTimeSource.CurrentTime, 60),
            (occurrence.LocalDate, occurrence.LocalTime, occurrence.TimeSource, occurrence.UtcOffsetMinutes));
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 9, 41, 0, TimeSpan.Zero), occurrence.OccurredAtUtc);
    }

    [Fact]
    public void AnotherDayWithoutATime_IsNoon_AndTheOffsetIsThatDaysOne()
    {
        // 2026-07-15 is summer time in Rome (+02:00); today (27 Oct) is +01:00.
        var occurrence = AssistantDiaryOccurrenceResolver.Resolve(new DateOnly(2026, 7, 15), null, "Europe/Rome", Now)!;

        Assert.Equal((new TimeOnly(12, 0), AssistantDraftTimeSource.Noon, 120), (occurrence.LocalTime, occurrence.TimeSource, occurrence.UtcOffsetMinutes));
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), occurrence.OccurredAtUtc);
    }

    [Fact]
    public void AnExplicitTime_IsKept_AndWithoutAValidZoneNothingIsResolved()
    {
        var occurrence = AssistantDiaryOccurrenceResolver.Resolve(null, new TimeOnly(8, 30), "America/New_York", Now)!;

        Assert.Equal((new TimeOnly(8, 30), AssistantDraftTimeSource.Explicit, -240), (occurrence.LocalTime, occurrence.TimeSource, occurrence.UtcOffsetMinutes));
        Assert.Null(AssistantDiaryOccurrenceResolver.Resolve(null, null, null, Now));
        Assert.Null(AssistantDiaryOccurrenceResolver.Resolve(null, null, "Not/AZone", Now));
    }

    [Fact]
    public void AWaterIntakeOnAFutureDay_BlocksTheConfirmation_AMealDoesNot()
    {
        var tomorrow = AssistantDiaryOccurrenceResolver.Resolve(new DateOnly(2026, 10, 28), null, "Europe/Rome", Now);

        Assert.Equal([HydrationOperationErrors.FutureDate],
            new HydrationDraftPolicy(null!).Issues(HydrationIntakeDraft.Create(300, new DateOnly(2026, 10, 28), null, tomorrow), Now));
        Assert.Empty(new MealDraftPolicy(null!).Issues(MealDraft.Create("pizza", null, new DateOnly(2026, 10, 28), null, tomorrow), Now));
        Assert.True(new MealDraftPolicy(null!).OccursInFuture(MealDraft.Create("pizza", null, new DateOnly(2026, 10, 28), null, tomorrow), Now));
    }

    // ---- Protocol v2 rules (.NET side; the golden cases are in AssistantContractTests) ----

    [Fact]
    public void OnlyTheIntentsSection_MayBeFilled()
    {
        const string message = "Speso 5 euro e bevuto 300 ml";
        var water = Water(Stated("300", "300 ml"), Stated("ml"));

        Assert.Null(AssistantInterpretationValidator.Validate(Drink(water), message));
        Assert.Equal("section_mismatch", AssistantInterpretationValidator.Validate(
            Of(AssistantIntent.FinanceExpense, Fields(amount: Stated("5", "5 euro")), hydration: water), message));
        Assert.Equal("section_mismatch", AssistantInterpretationValidator.Validate(
            Of(AssistantIntent.DraftUpdate, Fields(amount: Stated("5", "5 euro")), hydration: water), message));
        Assert.Null(AssistantInterpretationValidator.Validate(UpdateWater(water), message));
    }

    [Fact]
    public void AMealDescription_IsTheUsersOwnWords_AndNothingIsEverInferredOutsideFinance()
    {
        const string message = "A pranzo ho mangiato una pizza";

        Assert.Null(AssistantInterpretationValidator.Validate(Meal(MealFields(Stated("una pizza"), Stated("lunch", "pranzo"))), message));
        Assert.Equal("meal_description_not_verbatim",
            AssistantInterpretationValidator.Validate(Meal(MealFields(Stated("una pizza con 200 g di mozzarella", "una pizza"))), message));
        Assert.Equal("meal_meal_type_inferred",
            AssistantInterpretationValidator.Validate(Meal(MealFields(Stated("una pizza"), Inferred("lunch"))), message));
        Assert.Equal("hydration_amount_inferred",
            AssistantInterpretationValidator.Validate(Drink(Water(Inferred("250"), Stated("ml", "mangiato"))), message));
    }

    // ---- Routing ----

    [Fact]
    public async Task GeneralIntents_AreTheCores_AndNameOnlyTheEnabledModules()
    {
        var router = Router(null, Module("finance.transaction.create", "spese ed entrate", AssistantIntent.FinanceExpense),
            Module("hydration.intake.create", "l'acqua che bevi", AssistantIntent.HydrationIntake));

        var question = await ApplyAsync(router, Of(AssistantIntent.GeneralQuestion));
        var reminder = await ApplyAsync(router, Of(AssistantIntent.Unsupported, reason: AssistantUnsupportedReason.OtherCapability));

        Assert.StartsWith("Per ora posso aiutarti solo a registrare spese ed entrate e l'acqua che bevi.", question.Reply);
        Assert.StartsWith("Questa funzione non è ancora disponibile", reminder.Reply);
    }

    [Fact]
    public async Task AFinanceOnlyRouter_GivesTheAI007Texts()
    {
        var router = Router(null, Module("finance.transaction.create", "spese ed entrate", AssistantIntent.FinanceExpense));

        Assert.Equal("Per ora posso aiutarti solo a registrare spese ed entrate. Non posso ancora consultare saldi, movimenti o altri dati di LifeOS, "
            + "quindi non posso rispondere a questa domanda.", (await ApplyAsync(router, Of(AssistantIntent.GeneralQuestion))).Reply);
    }

    [Fact]
    public async Task ANewRecord_GoesToItsModule_OrIsNotAvailable()
    {
        var finance = Module("finance.transaction.create", "spese ed entrate", AssistantIntent.FinanceExpense);
        var water = Module("hydration.intake.create", "l'acqua che bevi", AssistantIntent.HydrationIntake);
        var router = Router(null, finance, water);

        await ApplyAsync(router, Drink(Water()));
        var meal = await ApplyAsync(router, Meal(MealFields(Stated("pasta"))));

        Assert.Equal((0, 1), (finance.Applied.Count, water.Applied.Count));
        Assert.StartsWith("Questa funzione non è ancora disponibile", meal.Reply);
    }

    [Fact]
    public async Task DraftIntents_GoToTheOpenDraftsModule_WhateverItsKind()
    {
        var finance = Module("finance.transaction.create", "spese ed entrate", AssistantIntent.FinanceExpense);
        var water = Module("hydration.intake.create", "l'acqua che bevi", AssistantIntent.HydrationIntake);
        var router = Router(water.Kind, finance, water);

        foreach (var intent in new[] { AssistantIntent.DraftUpdate, AssistantIntent.DraftConfirm, AssistantIntent.DraftCancel, AssistantIntent.Clarification })
        {
            await ApplyAsync(router, Of(intent), water.Open);
        }

        Assert.Equal((0, 4), (finance.Applied.Count, water.Applied.Count));
    }

    [Fact]
    public async Task WithoutADraft_DraftIntentsAreAnsweredByTheCore()
    {
        var finance = Module("finance.transaction.create", "spese ed entrate", AssistantIntent.FinanceExpense);
        var router = Router(null, finance);

        Assert.Equal(AssistantCoreReplies.NothingToUpdate, (await ApplyAsync(router, Of(AssistantIntent.DraftUpdate))).Reply);
        Assert.Equal(AssistantCoreReplies.NothingToConfirm, (await ApplyAsync(router, Of(AssistantIntent.DraftConfirm))).Reply);
        Assert.Equal(AssistantCoreReplies.NothingToCancel, (await ApplyAsync(router, Of(AssistantIntent.DraftCancel))).Reply);
        Assert.Equal(AssistantCoreReplies.NotUnderstood, (await ApplyAsync(router, Of(AssistantIntent.Clarification))).Reply);
        Assert.Empty(finance.Applied);
    }

    [Fact]
    public async Task ANewRecordOfAnotherModule_WhileADraftIsOpen_AsksToFinishItFirst()
    {
        var finance = Module("finance.transaction.create", "spese ed entrate", AssistantIntent.FinanceExpense, draftName: "transazione");
        var water = Module("hydration.intake.create", "l'acqua che bevi", AssistantIntent.HydrationIntake);
        var router = Router(finance.Kind, finance, water);

        var outcome = await ApplyAsync(router, Drink(Water()), finance.Open);

        Assert.Equal(AssistantCoreReplies.OtherDraftOpen("transazione"), outcome.Reply);
        Assert.Equal((0, 0), (finance.Applied.Count, water.Applied.Count));
    }

    [Fact]
    public async Task ADraftThatChangedWhileInterpreting_AppliesNothing()
    {
        var water = Module("hydration.intake.create", "l'acqua che bevi", AssistantIntent.HydrationIntake);
        var router = Router(water.Kind, water);

        var outcome = await router.ApplyAsync(Guid.CreateVersion7(), Guid.CreateVersion7(),
            new AssistantTurnPreparation(Context(null), new AssistantOperationSnapshot(water.Open.Snapshot.OperationId, 1)), UpdateWater(Water()),
            CancellationToken.None);

        Assert.Equal(AssistantErrors.DraftChanged, outcome.FailureCode);
        Assert.Empty(water.Applied);
    }

    [Fact]
    public async Task TheContext_CarriesTheOpenDraftAndOnlyWhatModulesAdd()
    {
        var water = Module("hydration.intake.create", "l'acqua che bevi", AssistantIntent.HydrationIntake);
        var router = Router(water.Kind, water);

        var preparation = await router.PrepareAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(water.Open.Context, preparation.Context.OpenDraft);
        Assert.Equal(AssistantFinanceContext.None, preparation.Context.Finance);
        Assert.Equal(water.Open.Snapshot, preparation.OpenOperation);
    }

    // ---- Helpers ----

    private static AssistantInterpretationContext Context(AssistantOpenDraftContext? open) =>
        new(AssistantInterpretationProtocol.Locale, open, AssistantFinanceContext.None);

    private static Task<AssistantCapabilityOutcome> ApplyAsync(AssistantCapabilityRouter router, AssistantInterpretation interpretation,
        AssistantOpenDraft? open = null) =>
        router.ApplyAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), new AssistantTurnPreparation(Context(open?.Context), open?.Snapshot),
            interpretation, CancellationToken.None);

    private static AssistantCapabilityRouter Router(string? openKind, params FakeModule[] modules) =>
        new(new FakeReader(openKind, modules.FirstOrDefault(module => module.Kind == openKind)?.Open), modules);

    private static FakeModule Module(string kind, string label, AssistantIntent opens, string draftName = "bozza") => new(kind, label, opens, draftName);

    private sealed class FakeReader(string? openKind, AssistantOpenDraft? open) : IAssistantOperationReader
    {
        public Task<AssistantOperationHeader?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssistantOperationHeader?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(openKind is null || open is null
                ? null
                : new AssistantOperationHeader(open.Snapshot.OperationId, openKind, AssistantOperationStatus.Collecting, open.Snapshot.DraftVersion,
                    Now.AddHours(1)));

        public Task<bool> TryExpireAsync(Guid userId, Guid operationId, AssistantOperationStatus expectedStatus, int expectedDraftVersion,
            DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeModule(string kind, string label, AssistantIntent opens, string draftName) : IAssistantModule
    {
        public AssistantOpenDraft Open { get; } = new(new AssistantOperationSnapshot(Guid.CreateVersion7(), 2),
            new AssistantOpenDraftContext(kind, null, [], ["amount"], "amount"));

        public List<AssistantIntent> Applied { get; } = [];

        public string Kind => kind;

        public string Label => label;

        public string DraftName => draftName;

        public bool Opens(AssistantIntent intent) => intent == opens;

        public Task<AssistantOpenDraft?> GetOpenDraftAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult<AssistantOpenDraft?>(Open);

        public Task ContributeAsync(Guid userId, AssistantOpenDraft? open, AssistantContextSections context, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<AssistantCapabilityOutcome> ApplyAsync(Guid userId, Guid conversationId, AssistantOpenDraft? open,
            AssistantInterpretation interpretation, CancellationToken cancellationToken)
        {
            Applied.Add(interpretation.Intent);
            return Task.FromResult(AssistantCapabilityOutcome.Answered(kind));
        }
    }
}
