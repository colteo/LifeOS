using LifeOS.Application.Assistant;
using static LifeOS.UnitTests.Fakes.Interpretations;

namespace LifeOS.UnitTests.Assistant;

// AI-007 F3: the backend's protocol check. Anything that breaks a rule invalidates the whole interpretation;
// nothing is repaired.
public class AssistantInterpretationValidatorTests
{
    private const string Message = "Ieri ho speso 5,50 euro per colazione con Pamela dal conto Revolut alle 8:30";

    [Fact]
    public void AFullExpense_WithEveryStatedValueFoundInTheMessage_IsValid()
    {
        var interpretation = Expense(Fields(
            amount: Stated("5.50", "5,50 euro"),
            currency: Stated("EUR", "euro"),
            account: Stated("Revolut", "conto revolut"),
            category: Inferred("Ristoranti"),
            note: Stated("colazione con Pamela"),
            date: Date(AssistantDateKind.Yesterday, "IERI"),
            time: Time(8, 30, "alle  8:30")));

        Assert.Null(AssistantInterpretationValidator.Validate(interpretation, Message));
    }

    public static TheoryData<string, AssistantInterpretation> Invalid => new()
    {
        { "protocol_version", Expense(Fields()) with { ProtocolVersion = 1 } },
        { "intent", Expense(Fields()) with { Intent = (AssistantIntent)99 } },
        { "finance_missing", new AssistantInterpretation(2, AssistantIntent.FinanceExpense, null!, AssistantHydrationFields.None, AssistantMealFields.None, [], null) },
        { "ambiguous_fields", Of(AssistantIntent.DraftUpdate, Fields(), ambiguous: ["accountId"]) },
        { "ambiguous_fields", Of(AssistantIntent.DraftUpdate, Fields(), ambiguous: ["account", "account"]) },
        { "unsupported_reason", Of(AssistantIntent.Unsupported) },
        { "unsupported_reason", Of(AssistantIntent.GeneralQuestion, reason: AssistantUnsupportedReason.Transfer) },
        // A stated value must come from the message.
        { "amount_span_not_in_message", Expense(Fields(amount: Stated("7"))) },
        { "account_span_not_in_message", Expense(Fields(account: Stated("Fineco"))) },
        { "account_span", Expense(Fields(account: new AssistantTextField(AssistantFieldState.Stated, "Revolut"))) },
        // The model may propose a category or a note, never an amount, an account, a date or a time.
        { "amount_inferred", Expense(Fields(amount: Inferred("5"))) },
        { "account_inferred", Expense(Fields(account: Inferred("Revolut"))) },
        { "date_inferred", Expense(Fields(date: new AssistantDateField(AssistantFieldState.Inferred, AssistantDateKind.Today))) },
        // Formats.
        { "amount_format", Expense(Fields(amount: Stated("5,50"))) },
        { "amount_format", Expense(Fields(amount: Stated("-5", "5"))) },
        { "amount_format", Expense(Fields(amount: Stated("5.12345", "5"))) },
        { "currency_format", Expense(Fields(currency: Stated("euro"))) },
        { "type_format", Update(Fields(type: Stated("transfer", "speso"))) },
        { "type_contradicts_intent", Expense(Fields(type: Stated("income", "speso"))) },
        // States carry exactly their parts; only removable fields can be cleared.
        { "amount_clear", Update(Fields(amount: Clear)) },
        { "note_unexpected_value", Update(Fields(note: new AssistantTextField(AssistantFieldState.Absent, "x"))) },
        { "date_parts", Expense(Fields(date: Date(AssistantDateKind.Yesterday, "ieri", daysAgo: 1))) },
        { "date_parts", Expense(Fields(date: Date(AssistantDateKind.Calendar, "ieri", day: 31))) },
        { "date_parts", Expense(Fields(date: Date(AssistantDateKind.DaysAgo, "ieri", daysAgo: 400))) },
        { "time_parts", Expense(Fields(time: Time(24, 0, "alle 8:30"))) }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void ABrokenRule_InvalidatesTheInterpretation(string expected, AssistantInterpretation interpretation)
    {
        Assert.Equal(expected, AssistantInterpretationValidator.Validate(interpretation, Message));
    }

    // The canonical contract (docs/contracts/assistant-interpretation/v1): every interpretation carries the
    // Finance section, all fields Absent when the intent changes no draft; a missing one is refused for every
    // intent, as Python refuses it.
    [Fact]
    public void EveryIntent_CarriesTheFinanceSection()
    {
        Assert.Null(AssistantInterpretationValidator.Validate(Of(AssistantIntent.GeneralQuestion), Message));
        Assert.Null(AssistantInterpretationValidator.Validate(Of(AssistantIntent.Unsupported, reason: AssistantUnsupportedReason.Transfer), Message));
        Assert.Equal("finance_missing",
            AssistantInterpretationValidator.Validate(new AssistantInterpretation(2, AssistantIntent.GeneralQuestion, null!, AssistantHydrationFields.None, AssistantMealFields.None, [], null), Message));
        Assert.Equal("missing", AssistantInterpretationValidator.Validate(null, Message));
    }

    // The model can neither name a resource the user did not write nor reword the user's note (a name in it
    // keeps its case).
    [Theory]
    [InlineData("account_value_not_in_span", "account")]
    [InlineData("category_value_not_in_span", "category")]
    [InlineData("note_not_verbatim", "note")]
    public void AStatedValue_IsTheUsersOwnWords(string expected, string field)
    {
        var interpretation = Expense(field switch
        {
            "account" => Fields(account: Stated("Fineco", "dal conto Revolut")),
            "category" => Fields(category: Stated("Spesa", "colazione")),
            _ => Fields(note: Stated("colazione con pamela", "colazione con Pamela"))
        });

        Assert.Equal(expected, AssistantInterpretationValidator.Validate(interpretation, Message));
        Assert.Null(AssistantInterpretationValidator.Validate(Expense(Fields(note: Stated("colazione con Pamela"))), Message));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("12.50", 12.50)]
    [InlineData("0.0001", 0.0001)]
    public void Amounts_AreParsedInTheProtocolsInvariantFormat(string value, decimal expected)
    {
        Assert.True(AssistantInterpretationValidator.TryParseAmount(value, out var amount));
        Assert.Equal(expected, amount);
    }
}
