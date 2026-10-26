using System.Text.Json;
using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Persistence;
using LifeOS.Application.Users;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LifeOS.UnitTests.Fakes.Interpretations;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-007 F3-A: the deterministic orchestrator end to end on PostgreSQL — turn processor, Finance capability,
// F2 handlers, unit of work and real repositories — with a scripted interpreter (no model, no network). Every
// assertion about state reads the database (messages, operations, transactions), not only what the processor
// returned. Races are made deterministic by running the competing request inside the interpreter call (the
// moment between T1 and T2).
//
// The clock is manual: 2026-10-24 09:00Z (11:00 in Rome); users are in Europe/Rome unless stated.
[Collection(PostgreSqlCollection.Name)]
public class AssistantOrchestrationTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(Now);

    // ---- Multi-turn: the persisted draft carries the state ----

    [Fact]
    public async Task AnExpenseThenTheAccount_CompleteOneDraftOverTwoTurns_AndOnlyTheConfirmationWrites()
    {
        // One account only: it is still asked for (never preselected).
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        const string first = "Ho speso 5 euro per colazione con Pamela";
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("5", "5 euro"), currency: Stated("EUR", "euro"), category: Inferred("Ristoranti"),
                note: Stated("colazione con Pamela"))))
            .Then(Update(Fields(account: Stated("Revolut"))));

        var turn1 = await SendAsync(owner.User, conversation, first, interpreter);

        AssertCompleted(turn1, "Da quale conto?");
        var draft1 = (await ActiveAsync(owner.User, conversation))!;
        Assert.Equal((AssistantOperationStatus.Collecting, 1), (draft1.Operation.Status, draft1.Operation.DraftVersion));
        Assert.Equal((TransactionType.Expense, 5m, "EUR", "colazione con Pamela", (Guid?)null),
            (draft1.Operation.Draft.Type, draft1.Operation.Draft.Amount, draft1.Operation.Draft.CurrencyMention, draft1.Operation.Draft.Note,
                draft1.Operation.Draft.AccountId));
        Assert.Equal((owner.Expense, FinanceDraftCategorySource.Suggested), (draft1.Operation.Draft.CategoryId!.Value, draft1.Operation.Draft.CategorySource!.Value));

        var turn2 = await SendAsync(owner.User, conversation, "Revolut", interpreter);

        AssertCompleted(turn2, "Ho preparato la spesa. Vuoi modificarla?");
        var draft2 = (await ActiveAsync(owner.User, conversation))!;
        Assert.Equal((draft1.Operation.Id, AssistantOperationStatus.AwaitingConfirmation, 2, owner.Account),
            (draft2.Operation.Id, draft2.Operation.Status, draft2.Operation.DraftVersion, draft2.Operation.Draft.AccountId!.Value));
        Assert.True(draft2.CanConfirm);

        // The second request told the model which field was awaited (no values, no ids).
        var request2 = interpreter.Requests[1];
        Assert.Equal(("Revolut", "account"), (request2.Message, request2.Context.Finance.OpenDraft!.AwaitingField));
        Assert.Equal(["account"], request2.Context.Finance.OpenDraft.MissingFields);
        Assert.Null(interpreter.Requests[0].Context.Finance.OpenDraft);

        // The conversation reads in order; nothing was written to Finance.
        Assert.Equal([(AssistantMessageRole.User, first), (AssistantMessageRole.Assistant, "Da quale conto?"),
                (AssistantMessageRole.User, "Revolut"), (AssistantMessageRole.Assistant, "Ho preparato la spesa. Vuoi modificarla?")],
            (await MessagesAsync(conversation)).Select(message => (message.Role, message.Content)));
        Assert.Equal(0, await TransactionCountAsync(owner.User));

        // Only the explicit confirmation (F2) records it, exactly once.
        var confirmed = await ConfirmAsync(owner.User, draft2.Operation.Id, draft2.Operation.DraftVersion);
        Assert.Equal(AssistantResultStatus.Ok, confirmed.Status);
        Assert.Equal(1, await TransactionCountAsync(owner.User, 5m));
    }

    // AI-007 §16 / ADR-014 §4: what leaves LifeOS carries no identifier and no account.
    [Fact]
    public async Task TheInterpreterRequest_CarriesNoIdentifierAndNoAccount()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("5"))))
            .Then(Of(AssistantIntent.GeneralQuestion));

        await SendAsync(owner.User, conversation, "Speso 5", interpreter);
        await SendAsync(owner.User, conversation, "Quanto ho speso?", interpreter);

        foreach (var request in interpreter.Requests)
        {
            var json = JsonSerializer.Serialize(request);
            Assert.DoesNotContain(owner.User.Id.ToString(), json);
            Assert.DoesNotContain(conversation.ToString(), json);
            Assert.DoesNotContain(owner.Account.ToString(), json);
            Assert.DoesNotContain(owner.Expense.ToString(), json);
            Assert.DoesNotContain("Revolut", json);
            Assert.Equal(["Ristoranti"], request.Context.Finance.ExpenseCategories);
        }

        var open = interpreter.Requests[1].Context.Finance.OpenDraft!;
        Assert.Equal(("finance.transaction.create", "expense"), (open.Kind, open.Type));
        Assert.Equal(["type", "amount"], open.FilledFields);
        Assert.Equal(["account", "category"], open.MissingFields);
        // The draft's type is known: only expense categories are offered.
        Assert.Empty(interpreter.Requests[1].Context.Finance.IncomeCategories);
    }

    // A category name longer than the protocol allows is not offered to the model (the service would refuse the
    // whole request); the others still are.
    [Fact]
    public async Task ACategoryNameBeyondTheProtocolsLimit_IsNotSentToTheInterpreter()
    {
        var owner = await NewOwnerAsync();
        await NewCategoryAsync(owner.User, CategoryType.Expense, new string('x', AssistantInterpretationProtocol.MaxCategoryNameLength + 1));
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter().Then(Of(AssistantIntent.GeneralQuestion));

        await SendAsync(owner.User, conversation, "Quanto ho speso?", interpreter);

        Assert.Equal(["Ristoranti"], interpreter.Requests.Single().Context.Finance.ExpenseCategories);
    }

    // ---- Account resolution ----

    [Fact]
    public async Task SameNameAccounts_AreAmbiguous_AnUnknownOneIsNotFound_AndAnotherUsersIsNeverSeen()
    {
        var owner = await NewOwnerAsync();
        var intruder = await NewOwnerAsync();
        await NewAccountAsync(owner.User, "Revolut");
        await NewAccountAsync(intruder.User, "Hype");
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("12"), account: Stated("Revolut"))))
            .Then(Update(Fields(account: Stated("Fineco"))))
            .Then(Update(Fields(account: Stated("Hype"))));

        AssertCompleted(await SendAsync(owner.User, conversation, "Speso 12 con Revolut", interpreter), "Ho trovato più conti «Revolut». Quale intendi?");
        AssertCompleted(await SendAsync(owner.User, conversation, "Fineco", interpreter), "Non trovo il conto «Fineco». Da quale conto?");
        AssertCompleted(await SendAsync(owner.User, conversation, "Hype", interpreter), "Non trovo il conto «Hype». Da quale conto?");

        var draft = (await ActiveAsync(owner.User, conversation))!;
        Assert.Null(draft.Operation.Draft.AccountId);
        // Nothing was applied by the two turns that only mentioned an account.
        Assert.Equal(1, draft.Operation.DraftVersion);
    }

    [Fact]
    public async Task AStatedCurrencyTheAccountDoesNotHave_IsAskedAgain_NeverConverted()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("10"), currency: Stated("USD", "dollari"), account: Stated("Revolut"))));

        AssertCompleted(await SendAsync(owner.User, conversation, "Speso 10 dollari con Revolut", interpreter),
            "Il conto «Revolut» è in EUR, non in USD. Quale conto vuoi usare?");

        var draft = (await ActiveAsync(owner.User, conversation))!.Operation.Draft;
        Assert.Equal(("USD", (Guid?)null), (draft.CurrencyMention, draft.AccountId));
    }

    // ---- Category: Suggested never replaces User ----

    [Fact]
    public async Task ASuggestedCategory_NeverReplacesTheUsersChoice()
    {
        var owner = await NewOwnerAsync();
        var groceries = await NewCategoryAsync(owner.User, CategoryType.Expense, "Spesa");
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("30"), category: Inferred("Ristoranti"))))
            .Then(Update(Fields(category: Stated("Spesa", "categoria spesa"))))
            .Then(Update(Fields(category: Inferred("Ristoranti"), note: Stated("supermercato"))));

        await SendAsync(owner.User, conversation, "Speso 30", interpreter);
        Assert.Equal((owner.Expense, FinanceDraftCategorySource.Suggested), Category(await ActiveAsync(owner.User, conversation)));

        await SendAsync(owner.User, conversation, "No, categoria spesa", interpreter);
        Assert.Equal((groceries, FinanceDraftCategorySource.User), Category(await ActiveAsync(owner.User, conversation)));

        await SendAsync(owner.User, conversation, "Era al supermercato", interpreter);
        var draft = await ActiveAsync(owner.User, conversation);
        Assert.Equal((groceries, FinanceDraftCategorySource.User), Category(draft));
        Assert.Equal("supermercato", draft!.Operation.Draft.Note);
    }

    // AI-007 F3-B2, live run 1: descriptions returned as stated categories. Nothing is applied as the user's choice unless
    // the user's words name it; the mention is asked about, never moved to the note or turned into a suggestion.
    [Fact]
    public async Task AStatedDescription_IsAsked_NeverAppliedAsTheUsersCategory_NorMovedToTheNote()
    {
        var owner = await NewOwnerAsync();
        var specialist = await NewCategoryAsync(owner.User, CategoryType.Expense, "Visite mediche specialistiche");
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("45.00", "45,00"), currency: Stated("EUR", "EUR"),
                category: Stated("visita medica", "di visita medica"))))
            .Then(Update(Fields(category: Stated("mediche specialistiche", "di mediche specialistiche"))))
            .Then(Update(Fields(category: Stated("mediche specialistiche", "categoria mediche specialistiche"))));

        var turn1 = await SendAsync(owner.User, conversation, "Pagati EUR 45,00 di visita medica", interpreter);

        AssertCompleted(turn1, "Non trovo la categoria «visita medica». Quale categoria?");
        var draft1 = (await ActiveAsync(owner.User, conversation))!.Operation.Draft;
        Assert.Equal((45.00m, "EUR", (Guid?)null, (string?)null), (draft1.Amount, draft1.CurrencyMention, draft1.CategoryId, draft1.Note));

        // Part of a name without the word "categoria": asked, not applied.
        var turn2 = await SendAsync(owner.User, conversation, "di mediche specialistiche", interpreter);

        AssertCompleted(turn2, "Quale categoria intendi per «mediche specialistiche»?");
        Assert.Null((await ActiveAsync(owner.User, conversation))!.Operation.Draft.CategoryId);

        // The same words with "categoria": the user's choice.
        await SendAsync(owner.User, conversation, "categoria mediche specialistiche", interpreter);

        var draft3 = await ActiveAsync(owner.User, conversation);
        Assert.Equal((specialist, FinanceDraftCategorySource.User), Category(draft3));
        Assert.Equal(AssistantOperationStatus.Collecting, draft3!.Operation.Status);
    }

    // AI-007 F3-B2: the categories the model chooses from are the user's own rows (here the starter tree, parents and
    // children, as onboarding creates it), never a fixed list nor another user's; a listed subcategory or its parent is a
    // suggestion; the user's explicit choice wins over any later suggestion; the description stays in the note.
    [Fact]
    public async Task TheStarterTree_IsOfferedFromTheUsersRows_AndASubcategoryOrItsParentIsOnlyASuggestion()
    {
        var owner = await NewOwnerAsync();
        var tree = StarterCategories.CreateMissing(owner.User.Id, [], Now.AddDays(-1));
        await PostgresAssert.InsertAsync(fixture, [.. tree]);
        var intruder = await NewOwnerAsync();
        await NewCategoryAsync(intruder.User, CategoryType.Expense, "Categoria altrui");
        Guid Id(string name) => tree.Single(category => category.Name == name && category.CategoryType == CategoryType.Expense).Id;
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("90", "90 euro"), currency: Stated("EUR", "euro"), category: Inferred("Medical"),
                note: Stated("il dentista"))))
            .Then(Update(Fields(category: Inferred("Health & Fitness"))))
            .Then(Update(Fields(category: Inferred("Categoria altrui"))))
            .Then(Update(Fields(category: Stated("Leisure", "categoria Leisure"))))
            .Then(Update(Fields(category: Inferred("Medical"))));

        await SendAsync(owner.User, conversation, "Ho pagato 90 euro per il dentista", interpreter);

        var offered = interpreter.Requests[0].Context.Finance;
        Assert.Equal(StarterCategories.All.Where(c => c.CategoryType == CategoryType.Expense).Sum(c => 1 + c.Children.Count) + 1,
            offered.ExpenseCategories.Count);
        Assert.Contains("Health & Fitness", offered.ExpenseCategories);
        Assert.Contains("Medical", offered.ExpenseCategories);
        Assert.Contains("Ristoranti", offered.ExpenseCategories);
        Assert.Contains("Gifts", offered.ExpenseCategories);
        Assert.Contains("Gifts", offered.IncomeCategories);
        Assert.DoesNotContain("Medical", offered.IncomeCategories);
        Assert.DoesNotContain("Categoria altrui", offered.ExpenseCategories);
        var draft1 = await ActiveAsync(owner.User, conversation);
        Assert.Equal((Id("Medical"), FinanceDraftCategorySource.Suggested), Category(draft1));
        Assert.Equal("il dentista", draft1!.Operation.Draft.Note);

        // The parent is the generic suggestion; another user's category is never one.
        await SendAsync(owner.User, conversation, "più in generale", interpreter);
        Assert.Equal((Id("Health & Fitness"), FinanceDraftCategorySource.Suggested), Category(await ActiveAsync(owner.User, conversation)));
        await SendAsync(owner.User, conversation, "quella dell'altro", interpreter);
        Assert.Equal((Id("Health & Fitness"), FinanceDraftCategorySource.Suggested), Category(await ActiveAsync(owner.User, conversation)));

        // The user's explicit choice, then a suggestion that must not replace it; the note is untouched.
        await SendAsync(owner.User, conversation, "categoria Leisure", interpreter);
        await SendAsync(owner.User, conversation, "era una visita", interpreter);
        var draft = await ActiveAsync(owner.User, conversation);
        Assert.Equal((Id("Leisure"), FinanceDraftCategorySource.User), Category(draft));
        Assert.Equal("il dentista", draft!.Operation.Draft.Note);
        Assert.Equal(0, await TransactionCountAsync(owner.User));
    }

    [Fact]
    public async Task AnIncome_TakesOnlyAnIncomeCategory_AndAsksOnWhichAccount()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Income(Fields(amount: Stated("1500"), category: Inferred("Ristoranti"))))
            .Then(Update(Fields(category: Inferred("Stipendio"))));

        AssertCompleted(await SendAsync(owner.User, conversation, "Ricevuto 1500", interpreter), "Su quale conto?");
        var draft = (await ActiveAsync(owner.User, conversation))!.Operation.Draft;
        // An expense category is no suggestion for an income.
        Assert.Equal((TransactionType.Income, (Guid?)null), (draft.Type, draft.CategoryId));

        await SendAsync(owner.User, conversation, "stipendio", interpreter);
        Assert.Equal((owner.Income, FinanceDraftCategorySource.Suggested), Category(await ActiveAsync(owner.User, conversation)));
    }

    // ---- Dates: resolved by the backend, in the user's zone, across DST ----

    [Fact]
    public async Task RelativeDates_AreResolvedOnTheBackend_InTheUsersZone_AcrossTheDstChange()
    {
        // 2026-10-25 is the end of DST in Rome (03:00 CEST → 02:00 CET): now is 11:00 CET.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("10"), date: Date(AssistantDateKind.Yesterday, "ieri"))))
            .Then(Update(Fields(date: Date(AssistantDateKind.Weekday, "lunedì scorso", weekday: DayOfWeek.Monday), time: Time(8, 30, "alle 8:30"))))
            .Then(Update(Fields(date: Date(AssistantDateKind.Calendar, "il 30 ottobre", day: 30, month: 10), time: new AssistantTimeField(AssistantFieldState.Clear),
                account: Stated("Revolut"), category: Inferred("Ristoranti"))));

        await SendAsync(owner.User, conversation, "Ieri ho speso 10", interpreter);
        var yesterday = (await ActiveAsync(owner.User, conversation))!.Operation.Draft.Occurrence!;
        // Yesterday was still CEST (+02:00): 12:00 local = 10:00Z.
        Assert.Equal((new DateOnly(2026, 10, 24), new TimeOnly(12, 0), FinanceDraftTimeSource.Noon, new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero)),
            (yesterday.LocalDate, yesterday.LocalTime, yesterday.TimeSource, yesterday.OccurredAtUtc));

        await SendAsync(owner.User, conversation, "No, lunedì scorso alle 8:30", interpreter);
        var monday = (await ActiveAsync(owner.User, conversation))!.Operation.Draft.Occurrence!;
        Assert.Equal((new DateOnly(2026, 10, 19), new TimeOnly(8, 30), new DateTimeOffset(2026, 10, 19, 6, 30, 0, TimeSpan.Zero)),
            (monday.LocalDate, monday.LocalTime, monday.OccurredAtUtc));

        // An explicit future date is accepted and flagged before confirming.
        var future = await SendAsync(owner.User, conversation, "Anzi il 30 ottobre, conto Revolut", interpreter);
        AssertCompleted(future, "Ho preparato la spesa. Vuoi modificarla? La data è nel futuro: controllala prima di confermare.");
        var occurrence = (await ActiveAsync(owner.User, conversation))!.Operation.Draft.Occurrence!;
        Assert.Equal((new DateOnly(2026, 10, 30), new TimeOnly(12, 0), new DateTimeOffset(2026, 10, 30, 11, 0, 0, TimeSpan.Zero)),
            (occurrence.LocalDate, occurrence.LocalTime, occurrence.OccurredAtUtc));
    }

    [Fact]
    public async Task WithoutATimeZone_ARelativeDateIsNotGuessed()
    {
        var owner = await NewOwnerAsync(timeZone: null);
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("10"), date: Date(AssistantDateKind.Yesterday, "ieri"))));

        AssertCompleted(await SendAsync(owner.User, conversation, "Ieri 10", interpreter),
            "Imposta il fuso orario nelle impostazioni per usare date come «ieri».");
        var draft = (await ActiveAsync(owner.User, conversation))!.Operation.Draft;
        Assert.Equal(((DateOnly?)null, (FinanceDraftOccurrence?)null), (draft.RequestedDate, draft.Occurrence));
    }

    // ---- Confirmation and cancellation never come from text ----

    [Fact]
    public async Task ConfirmAndCancelInChat_NeverConfirmNorCancel()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("5"), account: Stated("Revolut"), category: Stated("Ristoranti"))))
            .Then(Of(AssistantIntent.FinanceDraftConfirm))
            .Then(Of(AssistantIntent.FinanceDraftCancel));

        AssertCompleted(await SendAsync(owner.User, conversation, "5 euro Ristoranti con Revolut", interpreter), "Ho preparato la spesa. Vuoi modificarla?");
        AssertCompleted(await SendAsync(owner.User, conversation, "Confermo", interpreter), FinanceAssistantReplies.ConfirmWithButton);
        AssertCompleted(await SendAsync(owner.User, conversation, "Annulla", interpreter), FinanceAssistantReplies.CancelWithButton);

        var draft = (await ActiveAsync(owner.User, conversation))!;
        Assert.Equal((AssistantOperationStatus.AwaitingConfirmation, 1), (draft.Operation.Status, draft.Operation.DraftVersion));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
    }

    [Fact]
    public async Task QuestionsAndUnsupportedRequests_AreAnswered_WithoutTouchingFinanceOrDrafts()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Of(AssistantIntent.GeneralQuestion))
            .Then(Of(AssistantIntent.Unsupported, reason: AssistantUnsupportedReason.Transfer))
            .Then(Of(AssistantIntent.Clarification))
            .Then(Of(AssistantIntent.FinanceDraftUpdate, Fields(amount: Stated("7"))));

        AssertCompleted(await SendAsync(owner.User, conversation, "Quanto ho speso questo mese?", interpreter), FinanceAssistantReplies.GeneralQuestion);
        AssertCompleted(await SendAsync(owner.User, conversation, "Sposta 100 euro su Fineco", interpreter),
            FinanceAssistantReplies.Unsupported(AssistantUnsupportedReason.Transfer));
        AssertCompleted(await SendAsync(owner.User, conversation, "boh", interpreter), FinanceAssistantReplies.NotUnderstood);
        AssertCompleted(await SendAsync(owner.User, conversation, "Metti 7", interpreter), FinanceAssistantReplies.NothingToUpdate);

        Assert.Null(await ActiveAsync(owner.User, conversation));
        Assert.Equal(0, await OperationCountAsync(conversation));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
    }

    [Fact]
    public async Task ANewExpenseWhileADraftIsOpen_DoesNotStartAnother()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("5"))))
            .Then(Expense(Fields(amount: Stated("9"))));

        await SendAsync(owner.User, conversation, "Speso 5", interpreter);
        AssertCompleted(await SendAsync(owner.User, conversation, "Speso 9", interpreter), FinanceAssistantReplies.AlreadyOpen);

        Assert.Equal(1, await OperationCountAsync(conversation));
        Assert.Equal(5m, (await ActiveAsync(owner.User, conversation))!.Operation.Draft.Amount);
    }

    [Fact]
    public async Task FieldsTheModelFoundAmbiguous_AreAsked_NotApplied()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Of(AssistantIntent.FinanceExpense, Fields(amount: Stated("5"), account: Stated("Revolut")), ambiguous: ["account"]));

        AssertCompleted(await SendAsync(owner.User, conversation, "5 forse Revolut", interpreter), "Non ho capito il conto. Da quale conto?");
        var draft = (await ActiveAsync(owner.User, conversation))!.Operation.Draft;
        Assert.Equal((5m, (Guid?)null), (draft.Amount, draft.AccountId));
    }

    // ---- Interpreter failures: the turn fails, nothing is applied, no reply is stored ----

    public static TheoryData<AssistantInterpretationFailure, string> Failures => new()
    {
        { AssistantInterpretationFailure.Unavailable, AssistantErrors.AssistantUnavailable },
        { AssistantInterpretationFailure.Timeout, AssistantErrors.AssistantTimeout },
        { AssistantInterpretationFailure.InvalidOutput, AssistantErrors.AssistantInvalidOutput },
        { AssistantInterpretationFailure.Refused, AssistantErrors.AssistantRefused }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AnInterpreterFailure_FailsTheTurn_AndLeavesTheDraftUntouched(AssistantInterpretationFailure failure, string code)
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();
        var interpreter = new ScriptedAssistantInterpreter().Then(failure);

        var turn = await SendAsync(owner.User, conversation, "Revolut", interpreter);

        AssertFailed(turn, code);
        await AssertDraftVersionAsync(owner.User, conversation, version);
        Assert.Equal(AssistantMessageRole.User, (await MessagesAsync(conversation))[^1].Role);
    }

    // A structurally readable answer that breaks the protocol: a stated value that is not in the message, an
    // account "inferred" by the model, another protocol version.
    public static TheoryData<string> Malformed => new() { "span", "inferred_account", "version" };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task AMalformedInterpretation_FailsTheTurn_AndIsNeverRepaired(string kind)
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();
        var interpretation = kind switch
        {
            "span" => Update(Fields(account: Stated("Revolut", "conto Revolut"))),
            "inferred_account" => Update(Fields(account: Inferred("Revolut"))),
            _ => Update(Fields(account: Stated("Revolut"))) with { ProtocolVersion = 2 }
        };

        var turn = await SendAsync(owner.User, conversation, "Revolut", new ScriptedAssistantInterpreter().Then(interpretation));

        AssertFailed(turn, AssistantErrors.AssistantInvalidOutput);
        await AssertDraftVersionAsync(owner.User, conversation, version);
    }

    // An interpreter that never answers is cut at the turn's deadline (lease − CompletionMargin), so the
    // attempt can still record its failure itself.
    [Fact]
    public async Task AHangingInterpreter_IsCutAtTheTurnsDeadline()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter().ThenHang();

        // Right after preparing, the clock jumps to 200 ms before the deadline.
        var turn = await SendAsync(owner.User, conversation, "Speso 5", interpreter, capability: inner => new HookedCapability(inner,
            afterPrepare: () => _clock.Advance(AssistantTurnPolicy.LeaseDuration - AssistantTurnProcessor.CompletionMargin - TimeSpan.FromMilliseconds(200))));

        AssertFailed(turn, AssistantErrors.AssistantTimeout);
        Assert.Equal(0, await OperationCountAsync(conversation));
    }

    // ---- Concurrency, fencing, retries ----

    // The user edits the draft (PATCH) while the message is being interpreted: the late interpretation answers
    // a draft that no longer exists, so it is not applied over the edit; a retry interprets the current draft.
    [Fact]
    public async Task ADraftEditedWhileInterpreting_IsNeverOverwritten_AndARetryStartsFromTheCurrentDraft()
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();
        var operationId = (await ActiveAsync(owner.User, conversation))!.Operation.Id;
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Update(Fields(amount: Stated("9"), account: Stated("Revolut"))))
            .Then(Update(Fields(amount: Stated("9"), account: Stated("Revolut"))));
        interpreter.BeforeAnswer = async _ =>
        {
            interpreter.BeforeAnswer = null;
            Assert.Equal(AssistantResultStatus.Ok, (await PatchAsync(owner.User, operationId, version, new FinanceDraftPatch(Amount: 7m))).Status);
        };

        var turn = await SendAsync(owner.User, conversation, "9 euro con Revolut", interpreter);

        AssertFailed(turn, AssistantErrors.DraftChanged);
        var edited = (await ActiveAsync(owner.User, conversation))!.Operation;
        Assert.Equal((version + 1, 7m, (Guid?)null), (edited.DraftVersion, edited.Draft.Amount, edited.Draft.AccountId));

        var retried = await RetryAsync(owner.User, turn.Value!.UserMessage.Id, interpreter);

        AssertCompleted(retried, "Quale categoria?");
        var current = (await ActiveAsync(owner.User, conversation))!.Operation;
        Assert.Equal((version + 2, 9m, owner.Account), (current.DraftVersion, current.Draft.Amount, current.Draft.AccountId!.Value));
    }

    // A confirmation lands while the message is being interpreted: the late interpretation changes nothing.
    [Fact]
    public async Task ADraftConfirmedWhileInterpreting_IsNotChangedAfterwards()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("5"), account: Stated("Revolut"), category: Stated("Ristoranti"))))
            .Then(Update(Fields(amount: Stated("50"))));
        await SendAsync(owner.User, conversation, "5 Ristoranti Revolut", interpreter);
        var ready = (await ActiveAsync(owner.User, conversation))!.Operation;
        interpreter.BeforeAnswer = async _ =>
            Assert.Equal(AssistantResultStatus.Ok, (await ConfirmAsync(owner.User, ready.Id, ready.DraftVersion)).Status);

        AssertFailed(await SendAsync(owner.User, conversation, "anzi 50", interpreter), AssistantErrors.DraftChanged);

        Assert.Equal((1, 0), (await TransactionCountAsync(owner.User, 5m), await TransactionCountAsync(owner.User, 50m)));
        Assert.Null(await ActiveAsync(owner.User, conversation));
    }

    // A second message while the first is being interpreted is refused (one turn at a time): only one
    // interpretation is applied.
    [Fact]
    public async Task TwoMessagesAtOnce_OnlyOneIsProcessed()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter().Then(Expense(Fields(amount: Stated("5"))));
        AssistantResult<AssistantTurn>? concurrent = null;
        interpreter.BeforeAnswer = async _ =>
            concurrent = await SendAsync(owner.User, conversation, "Speso 9", new ScriptedAssistantInterpreter().Then(Expense(Fields(amount: Stated("9")))));

        AssertCompleted(await SendAsync(owner.User, conversation, "Speso 5", interpreter), "Da quale conto?");

        Assert.Equal(AssistantResultStatus.InProgress, concurrent!.Status);
        Assert.Equal(1, await OperationCountAsync(conversation));
        Assert.Equal(2, (await MessagesAsync(conversation)).Count);
    }

    // The first turn outlives its lease while interpreting; a later message is accepted (recovering the first)
    // and answered. The late first turn then changes nothing and stores no reply out of order.
    [Fact]
    public async Task ALateTurnSupersededByALaterMessage_ChangesNothing_AndRepliesStayInOrder()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter().Then(Expense(Fields(amount: Stated("5"))));
        AssistantResult<AssistantTurn>? later = null;
        interpreter.BeforeAnswer = async _ =>
        {
            _clock.Advance(AssistantTurnPolicy.LeaseDuration);
            later = await SendAsync(owner.User, conversation, "Quanto ho speso?", new ScriptedAssistantInterpreter().Then(Of(AssistantIntent.GeneralQuestion)));
        };

        var late = await SendAsync(owner.User, conversation, "Speso 5", interpreter);

        // The late attempt no longer holds its turn: it writes nothing (Busy, as any taken-over attempt).
        Assert.Equal(AssistantResultStatus.Busy, late.Status);
        AssertCompleted(later!, FinanceAssistantReplies.GeneralQuestion);
        Assert.Equal(0, await OperationCountAsync(conversation));
        var messages = await MessagesAsync(conversation);
        Assert.Equal([(AssistantMessageRole.User, "Speso 5"), (AssistantMessageRole.User, "Quanto ho speso?"),
            (AssistantMessageRole.Assistant, FinanceAssistantReplies.GeneralQuestion)], messages.Select(message => (message.Role, message.Content)));
        Assert.Equal((AssistantTurnStatus.Failed, AssistantMessage.Interrupted), (messages[0].TurnStatus!.Value, messages[0].FailureCode));
        Assert.Equal(messages[1].Id, messages[2].ReplyToMessageId);
    }

    // A replay of a completed turn (client timeout) returns the stored reply without interpreting or applying
    // again; a failed turn's retry applies the interpretation exactly once.
    [Fact]
    public async Task RetriesAndReplays_NeverApplyAnInterpretationTwice()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var clientId = Guid.CreateVersion7();
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(AssistantInterpretationFailure.Unavailable)
            .Then(Expense(Fields(amount: Stated("5"))));

        AssertFailed(await SendAsync(owner.User, conversation, "Speso 5", interpreter, clientId), AssistantErrors.AssistantUnavailable);
        // The client resends the same message: the failed turn is restarted automatically and now completes.
        var replayed = await SendAsync(owner.User, conversation, "Speso 5", interpreter, clientId);
        AssertCompleted(replayed, "Da quale conto?");
        Assert.Equal(2, replayed.Value!.UserMessage.TurnAttempts);

        // Again (the response was lost): the stored reply, no third interpretation, no second change.
        var again = await SendAsync(owner.User, conversation, "Speso 5", interpreter, clientId);
        Assert.Equal((AssistantResultStatus.Ok, true, replayed.Value.Reply!.Id), (again.Status, again.Value!.Replayed, again.Value.Reply!.Id));
        Assert.Equal(2, interpreter.Requests.Count);
        Assert.Equal(1, await OperationCountAsync(conversation));
        Assert.Equal(1, (await ActiveAsync(owner.User, conversation))!.Operation.DraftVersion);
        Assert.Equal(2, (await MessagesAsync(conversation)).Count);
    }

    [Fact]
    public async Task AnotherUsersConversation_IsNotFound_AndNothingIsInterpreted()
    {
        var owner = await NewOwnerAsync();
        var intruder = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter();

        var turn = await SendAsync(intruder.User, conversation, "Speso 5", interpreter);

        Assert.Equal(AssistantResultStatus.NotFound, turn.Status);
        Assert.Empty(interpreter.Requests);
        Assert.Empty(await MessagesAsync(conversation));
    }

    // The default composition (no configuration) registers the disabled interpreter: nothing is sent anywhere.
    [Fact]
    public async Task TheDefaultConfiguration_HasNoInterpreter_AndFailsTurnsAsUnavailable()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);

        await using var scope = fixture.CreateScope();
        Assert.Equal("DisabledAssistantInterpreter", scope.ServiceProvider.GetRequiredService<IAssistantInterpreter>().GetType().Name);

        var turn = await AssistantProcessorComposition.Processor(scope.ServiceProvider, _clock)
            .SendAsync(owner.User.Id, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), "Speso 5"), default);

        AssertFailed(turn, AssistantErrors.AssistantUnavailable);
        Assert.Equal(0, await OperationCountAsync(conversation));
    }


    // ---- Consolidation (F3-A): edits, expiry, cancellation and "sì" in chat ----

    // "No, 7 euro": the same draft takes the new amount in its next version; the other fields stay.
    [Fact]
    public async Task AnAmountCorrectedInChat_RevisesTheSameDraft_KeepingTheOtherFields()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Expense(Fields(amount: Stated("5", "5 euro"), note: Stated("colazione con Pamela"))))
            .Then(Update(Fields(amount: Stated("7", "7 euro"))));
        await SendAsync(owner.User, conversation, "Speso 5 euro per colazione con Pamela", interpreter);
        var before = (await ActiveAsync(owner.User, conversation))!.Operation;

        AssertCompleted(await SendAsync(owner.User, conversation, "No, 7 euro", interpreter), "Da quale conto?");

        var after = (await ActiveAsync(owner.User, conversation))!.Operation;
        Assert.Equal((before.Id, before.DraftVersion + 1, 7m, "colazione con Pamela"),
            (after.Id, after.DraftVersion, after.Draft.Amount, after.Draft.Note));
        Assert.Equal(1, await OperationCountAsync(conversation));
    }

    // An expired draft is never revived nor changed by a later message; a new expense opens a fresh draft.
    [Fact]
    public async Task AnExpiredDraft_IsNeitherRevivedNorChanged_AndANewExpenseOpensAnother()
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();
        var expired = (await ActiveAsync(owner.User, conversation))!.Operation;
        _clock.Advance(FinanceOperationLifetime + TimeSpan.FromSeconds(1));
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Update(Fields(account: Stated("Revolut"))))
            .Then(Expense(Fields(amount: Stated("9"))));

        AssertCompleted(await SendAsync(owner.User, conversation, "Revolut", interpreter), FinanceAssistantReplies.NothingToUpdate);
        var stored = (await GetOperationAsync(owner.User, expired.Id))!.Operation;
        Assert.Equal((AssistantOperationStatus.Expired, version, (Guid?)null), (stored.Status, stored.DraftVersion, stored.Draft.AccountId));

        AssertCompleted(await SendAsync(owner.User, conversation, "Speso 9", interpreter), "Da quale conto?");
        var fresh = (await ActiveAsync(owner.User, conversation))!.Operation;
        Assert.NotEqual(expired.Id, fresh.Id);
        Assert.Equal((9m, 2), (fresh.Draft.Amount, await OperationCountAsync(conversation)));
    }

    // The draft expires while the message is being interpreted: the late answer is not applied to it.
    [Fact]
    public async Task ADraftThatExpiresWhileInterpreting_IsNotChanged()
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();
        var draft = (await ActiveAsync(owner.User, conversation))!.Operation;
        _clock.UtcNow = draft.ExpiresAtUtc - TimeSpan.FromSeconds(10);
        var interpreter = new ScriptedAssistantInterpreter().Then(Update(Fields(account: Stated("Revolut"))));
        interpreter.BeforeAnswer = _ =>
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            return Task.CompletedTask;
        };

        AssertFailed(await SendAsync(owner.User, conversation, "Revolut", interpreter), AssistantErrors.DraftChanged);

        var stored = (await GetOperationAsync(owner.User, draft.Id))!.Operation;
        Assert.Equal((AssistantOperationStatus.Expired, version, (Guid?)null), (stored.Status, stored.DraftVersion, stored.Draft.AccountId));
    }

    // Cancelled with the button while interpreting, then written to afterwards: the draft never changes.
    [Fact]
    public async Task ACancelledDraft_IsNeverChangedByChat()
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();
        var draft = (await ActiveAsync(owner.User, conversation))!.Operation;
        var interpreter = new ScriptedAssistantInterpreter()
            .Then(Update(Fields(account: Stated("Revolut"))))
            .Then(Update(Fields(account: Stated("Revolut"))));
        interpreter.BeforeAnswer = async _ =>
        {
            interpreter.BeforeAnswer = null;
            Assert.Equal(AssistantResultStatus.Ok, (await CancelAsync(owner.User, draft.Id, version)).Status);
        };

        AssertFailed(await SendAsync(owner.User, conversation, "Revolut", interpreter), AssistantErrors.DraftChanged);
        AssertCompleted(await SendAsync(owner.User, conversation, "Revolut", interpreter), FinanceAssistantReplies.NothingToUpdate);

        var stored = (await GetOperationAsync(owner.User, draft.Id))!.Operation;
        Assert.Equal((AssistantOperationStatus.Cancelled, version, (Guid?)null), (stored.Status, stored.DraftVersion, stored.Draft.AccountId));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
    }

    // "Sì" on a draft that is not ready: no confirmation, no change, the missing field is asked.
    [Fact]
    public async Task ATextualYes_OnADraftNotReady_NeitherConfirmsNorChangesIt()
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();

        AssertCompleted(await SendAsync(owner.User, conversation, "Sì", new ScriptedAssistantInterpreter().Then(Of(AssistantIntent.FinanceDraftConfirm))),
            "Non è ancora pronta. Da quale conto?");

        var draft = (await ActiveAsync(owner.User, conversation))!.Operation;
        Assert.Equal((AssistantOperationStatus.Collecting, version), (draft.Status, draft.DraftVersion));
        Assert.Equal(0, await TransactionCountAsync(owner.User));
    }

    // ---- Consolidation (F3-A): races inside T2, replays while processing, crashes ----

    // A PATCH commits inside the turn's transaction, after the capability checked the snapshot and before
    // its conditional update: the update writes nothing, the use case reads the new version and refuses, the
    // whole turn rolls back (no reply) and fails with draft_changed. The PATCH is kept as written.
    [Fact(Timeout = 60_000)]
    public async Task APatchCommittedInsideTheTurnsTransaction_Wins_AndTheTurnStoresNoReply()
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();
        var operationId = (await ActiveAsync(owner.User, conversation))!.Operation.Id;
        var interpreter = new ScriptedAssistantInterpreter().Then(Update(Fields(amount: Stated("9"), account: Stated("Revolut"))));

        await using var scope = fixture.CreateScope();
        var turn = await AssistantProcessorComposition.Processor(scope.ServiceProvider, _clock, interpreter,
                operations: inner => new PatchBeforeFirstUpdate(inner, async () =>
                    Assert.Equal(AssistantResultStatus.Ok, (await PatchAsync(owner.User, operationId, version, new FinanceDraftPatch(Amount: 7m))).Status)))
            .SendAsync(owner.User.Id, conversation, new SendAssistantMessageCommand(Guid.CreateVersion7(), "9 euro con Revolut"), default);

        AssertFailed(turn, AssistantErrors.DraftChanged);
        var draft = (await ActiveAsync(owner.User, conversation))!.Operation;
        Assert.Equal((version + 1, 7m, (Guid?)null), (draft.DraftVersion, draft.Draft.Amount, draft.Draft.AccountId));
        Assert.Equal(AssistantMessageRole.User, (await MessagesAsync(conversation))[^1].Role);
    }

    // The client times out and resends the same message while it is still being interpreted: the replay is
    // "in progress" and interprets nothing; the first request applies its answer once; a later replay returns
    // the stored reply.
    [Fact]
    public async Task AReplayWhileTheTurnIsProcessing_InterpretsNothing_AndTheAnswerIsAppliedOnce()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        var clientId = Guid.CreateVersion7();
        var replayInterpreter = new ScriptedAssistantInterpreter();
        var interpreter = new ScriptedAssistantInterpreter().Then(Expense(Fields(amount: Stated("5"))));
        AssistantResult<AssistantTurn>? replay = null;
        interpreter.BeforeAnswer = async _ => replay = await SendAsync(owner.User, conversation, "Speso 5", replayInterpreter, clientId);

        AssertCompleted(await SendAsync(owner.User, conversation, "Speso 5", interpreter, clientId), "Da quale conto?");

        Assert.Equal(AssistantResultStatus.InProgress, replay!.Status);
        Assert.Empty(replayInterpreter.Requests);
        var again = await SendAsync(owner.User, conversation, "Speso 5", replayInterpreter, clientId);
        Assert.Equal((AssistantResultStatus.Ok, true, "Da quale conto?"), (again.Status, again.Value!.Replayed, again.Value.Reply!.Content));
        Assert.Empty(replayInterpreter.Requests);
        Assert.Equal((1, 1), (await OperationCountAsync(conversation), (await ActiveAsync(owner.User, conversation))!.Operation.DraftVersion));
    }

    // A crash inside T2 after the draft was written (here: an exception right after the capability's
    // update): the transaction rolls back, nothing is applied and no reply exists; the turn stays Processing
    // only until its lease ends, then the resend of the same message is reprocessed and applied exactly once.
    [Fact]
    public async Task ACrashAfterTheDraftWrite_RollsEverythingBack_AndTheTurnIsRecoverable()
    {
        var (owner, conversation, version) = await WithOpenDraftAsync();
        var clientId = Guid.CreateVersion7();
        var crashing = new ScriptedAssistantInterpreter().Then(Update(Fields(account: Stated("Revolut"))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SendAsync(owner.User, conversation, "Revolut", crashing, clientId, inner => new CrashAfterApply(inner)));

        await AssertDraftVersionAsync(owner.User, conversation, version);
        var stuck = (await MessagesAsync(conversation))[^1];
        Assert.Equal((AssistantMessageRole.User, AssistantTurnStatus.Processing), (stuck.Role, stuck.TurnStatus!.Value));

        _clock.Advance(AssistantTurnPolicy.LeaseDuration);
        var recovered = await SendAsync(owner.User, conversation, "Revolut", new ScriptedAssistantInterpreter().Then(Update(Fields(account: Stated("Revolut")))),
            clientId);

        AssertCompleted(recovered, "Quale categoria?");
        Assert.Equal(2, recovered.Value!.UserMessage.TurnAttempts);
        var draft = (await ActiveAsync(owner.User, conversation))!.Operation;
        Assert.Equal((version + 1, owner.Account), (draft.DraftVersion, draft.Draft.AccountId!.Value));
    }

    // ---- Helpers ----

    private sealed record Owner(User User, Guid Account, Guid Expense, Guid Income);

    private static void AssertCompleted(AssistantResult<AssistantTurn> turn, string reply)
    {
        Assert.Equal(AssistantResultStatus.Ok, turn.Status);
        Assert.Equal((AssistantTurnStatus.Completed, reply), (turn.Value!.UserMessage.TurnStatus!.Value, turn.Value.Reply?.Content));
    }

    private static void AssertFailed(AssistantResult<AssistantTurn> turn, string code)
    {
        Assert.Equal(AssistantResultStatus.Ok, turn.Status);
        Assert.Equal((AssistantTurnStatus.Failed, code), (turn.Value!.UserMessage.TurnStatus!.Value, turn.Value.UserMessage.FailureCode));
        Assert.Null(turn.Value.Reply);
    }

    private static (Guid, FinanceDraftCategorySource) Category(FinanceOperationView? view) =>
        (view!.Operation.Draft.CategoryId!.Value, view.Operation.Draft.CategorySource!.Value);

    // An owner with an open draft (5 EUR expense, no account, no category).
    private async Task<(Owner, Guid Conversation, int Version)> WithOpenDraftAsync()
    {
        var owner = await NewOwnerAsync();
        var conversation = await NewConversationAsync(owner.User);
        await SendAsync(owner.User, conversation, "Speso 5", new ScriptedAssistantInterpreter().Then(Expense(Fields(amount: Stated("5")))));

        return (owner, conversation, (await ActiveAsync(owner.User, conversation))!.Operation.DraftVersion);
    }

    private async Task AssertDraftVersionAsync(User user, Guid conversation, int version)
    {
        var draft = (await ActiveAsync(user, conversation))!.Operation;
        Assert.Equal((version, (Guid?)null), (draft.DraftVersion, draft.Draft.AccountId));
    }

    private async Task<AssistantResult<AssistantTurn>> SendAsync(User user, Guid conversation, string text, IAssistantInterpreter interpreter,
        Guid? clientMessageId = null, Func<IAssistantCapability, IAssistantCapability>? capability = null)
    {
        await using var scope = fixture.CreateScope();
        return await AssistantProcessorComposition.Processor(scope.ServiceProvider, _clock, interpreter, capability: capability)
            .SendAsync(user.Id, conversation, new SendAssistantMessageCommand(clientMessageId ?? Guid.CreateVersion7(), text), default);
    }

    private async Task<AssistantResult<AssistantTurn>> RetryAsync(User user, Guid messageId, IAssistantInterpreter interpreter)
    {
        await using var scope = fixture.CreateScope();
        return await AssistantProcessorComposition.Processor(scope.ServiceProvider, _clock, interpreter).RetryAsync(user.Id, messageId, default);
    }

    private async Task<FinanceOperationView?> ActiveAsync(User user, Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        return (await new GetActiveFinanceOperationHandler(services.GetRequiredService<IFinanceOperationRepository>(), References(services), _clock)
            .HandleAsync(user.Id, conversation, default)).Value;
    }

    private async Task<AssistantResult<FinanceOperationOutcome>> PatchAsync(User user, Guid operationId, int version, FinanceDraftPatch patch)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        return await new UpdateFinanceDraftHandler(services.GetRequiredService<IFinanceOperationRepository>(), References(services),
            services.GetRequiredService<IUserRepository>(), _clock).HandleAsync(user.Id, operationId, version, patch, default);
    }

    private async Task<AssistantResult<FinanceOperationOutcome>> ConfirmAsync(User user, Guid operationId, int version)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        var create = new CreateTransactionHandler(services.GetRequiredService<IAccountRepository>(), services.GetRequiredService<ICategoryRepository>(),
            services.GetRequiredService<ITransactionRepository>(), _clock);
        return await new ConfirmFinanceOperationHandler(services.GetRequiredService<IFinanceOperationRepository>(), References(services), create,
            services.GetRequiredService<IUnitOfWork>(), _clock).HandleAsync(user.Id, operationId, version, default);
    }

    private static FinanceDraftReferences References(IServiceProvider services) =>
        new(services.GetRequiredService<IAccountRepository>(), services.GetRequiredService<ICategoryRepository>());

    private async Task<List<AssistantMessage>> MessagesAsync(Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().AssistantMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversation).OrderBy(message => message.Sequence).ToListAsync();
    }

    private async Task<int> OperationCountAsync(Guid conversation)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM assistant_operations WHERE conversation_id = {conversation}").SingleAsync();
    }

    private async Task<int> TransactionCountAsync(User user, decimal? amount = null)
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

        return amount is { } value
            ? await database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM transactions WHERE user_id = {user.Id} AND amount = {value}").SingleAsync()
            : await database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM transactions WHERE user_id = {user.Id}").SingleAsync();
    }

    private async Task<Owner> NewOwnerAsync(string? timeZone = "Europe/Rome")
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));

        if (timeZone is not null)
        {
            user.SetTimeZone(timeZone);
        }

        await PostgresAssert.InsertAsync(fixture, user);

        return new Owner(user, await NewAccountAsync(user, "Revolut"), await NewCategoryAsync(user, CategoryType.Expense, "Ristoranti"),
            await NewCategoryAsync(user, CategoryType.Income, "Stipendio"));
    }

    private async Task<Guid> NewAccountAsync(User user, string name, string currency = "EUR")
    {
        var account = Account.Create(user.Id, name, AccountType.BankAccount, currency, Now.AddDays(-1));
        await PostgresAssert.InsertAsync(fixture, account);

        return account.Id;
    }

    private async Task<Guid> NewCategoryAsync(User user, CategoryType type, string name)
    {
        var category = Domain.Finance.Categories.Category.Create(user.Id, name, type, null, Now.AddDays(-1));
        await PostgresAssert.InsertAsync(fixture, category);

        return category.Id;
    }

    private async Task<Guid> NewConversationAsync(User user)
    {
        var conversation = AssistantConversation.Create(user.Id, Guid.CreateVersion7(), Now);
        await PostgresAssert.InsertAsync(fixture, conversation);

        return conversation.Id;
    }

    private static readonly TimeSpan FinanceOperationLifetime = AssistantOperation<FinanceTransactionDraft>.DraftLifetime;

    private async Task<FinanceOperationView?> GetOperationAsync(User user, Guid operationId)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        return (await new GetFinanceOperationHandler(services.GetRequiredService<IFinanceOperationRepository>(), References(services), _clock)
            .HandleAsync(user.Id, operationId, default)).Value;
    }

    private async Task<AssistantResult<FinanceOperationOutcome>> CancelAsync(User user, Guid operationId, int version)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        return await new CancelFinanceOperationHandler(services.GetRequiredService<IFinanceOperationRepository>(), References(services), _clock)
            .HandleAsync(user.Id, operationId, version, default);
    }

    // Delegates to the real repository; before the first conditional update, runs `step` (another request,
    // in its own scope and connection, which commits on its own).
    private sealed class PatchBeforeFirstUpdate(IFinanceOperationRepository inner, Func<Task> step) : IFinanceOperationRepository
    {
        private Func<Task>? _step = step;

        public Task<FinanceOperationAddOutcome> TryAddAsync(AssistantOperation<FinanceTransactionDraft> operation, CancellationToken cancellationToken) =>
            inner.TryAddAsync(operation, cancellationToken);

        public Task<AssistantOperation<FinanceTransactionDraft>?> GetAsync(Guid userId, Guid operationId, CancellationToken cancellationToken) =>
            inner.GetAsync(userId, operationId, cancellationToken);

        public Task<AssistantOperation<FinanceTransactionDraft>?> GetOpenAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
            inner.GetOpenAsync(userId, conversationId, cancellationToken);

        public async Task<bool> TryUpdateAsync(AssistantOperation<FinanceTransactionDraft> operation, AssistantOperationStatus expectedStatus,
            int expectedDraftVersion, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _step, null) is { } run)
            {
                await run();
            }

            return await inner.TryUpdateAsync(operation, expectedStatus, expectedDraftVersion, cancellationToken);
        }

        public Task<bool> TryLockForConfirmationAsync(Guid userId, Guid operationId, int expectedDraftVersion, DateTimeOffset now,
            CancellationToken cancellationToken) =>
            inner.TryLockForConfirmationAsync(userId, operationId, expectedDraftVersion, now, cancellationToken);
    }

    // The real capability, then a crash: its draft write has been issued inside the turn's transaction.
    private sealed class CrashAfterApply(IAssistantCapability inner) : IAssistantCapability
    {
        public Task<AssistantTurnPreparation> PrepareAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
            inner.PrepareAsync(userId, conversationId, cancellationToken);

        public async Task<AssistantCapabilityOutcome> ApplyAsync(Guid userId, Guid conversationId, AssistantTurnPreparation preparation,
            AssistantInterpretation interpretation, CancellationToken cancellationToken)
        {
            await inner.ApplyAsync(userId, conversationId, preparation, interpretation, cancellationToken);
            throw new InvalidOperationException("Synthetic crash after the draft write.");
        }
    }

    // Delegates to the real capability, running a step right after PrepareAsync.
    private sealed class HookedCapability(IAssistantCapability inner, Action afterPrepare) : IAssistantCapability
    {
        public async Task<AssistantTurnPreparation> PrepareAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
        {
            var preparation = await inner.PrepareAsync(userId, conversationId, cancellationToken);
            afterPrepare();
            return preparation;
        }

        public Task<AssistantCapabilityOutcome> ApplyAsync(Guid userId, Guid conversationId, AssistantTurnPreparation preparation,
            AssistantInterpretation interpretation, CancellationToken cancellationToken) =>
            inner.ApplyAsync(userId, conversationId, preparation, interpretation, cancellationToken);
    }
}
