using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using static LifeOS.UnitTests.Fakes.Interpretations;

namespace LifeOS.UnitTests.Assistant;

// AI-007 F3: deterministic resolution of mentions and date expressions. Never the first candidate, never a
// default, never a date the model computed.
public class FinanceAssistantResolverTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- Accounts ----

    [Theory]
    [InlineData("Revolut")]
    [InlineData("revolut")]
    [InlineData("dal conto Revolut")]
    [InlineData("la carta Revolut")]
    public void AnAccountMention_ResolvesToTheOnlyMatchingAccount(string mention)
    {
        var revolut = NewAccount("Revolut");
        var result = FinanceMentionResolver.Account(mention, [NewAccount("Fineco"), revolut]);

        Assert.Equal((FinanceMentionMatch.Resolved, revolut), (result.Match, result.Value));
    }

    [Fact]
    public void Names_AreComparedWithoutAccentsAndPunctuation_AndAWholeWordPartOfANameMatches()
    {
        var postePay = NewAccount("Poste-Pay Evolution");
        var cassa = NewAccount("Cassa comune");

        Assert.Equal(postePay, FinanceMentionResolver.Account("poste pay evolution", [postePay, cassa]).Value);
        Assert.Equal(postePay, FinanceMentionResolver.Account("Evolution", [postePay, cassa]).Value);
        // No fuzzy matching: different words are a different name.
        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Account("postepay", [postePay, cassa]).Match);
        Assert.Equal(cassa, FinanceMentionResolver.Account("càssa", [postePay, cassa]).Value);
        // A fragment of a word is not a match.
        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Account("cass", [postePay, cassa]).Match);
    }

    [Fact]
    public void SameNameAccounts_OrAPartialMentionOfSeveral_AreAmbiguous_NeverTheFirst()
    {
        Assert.Equal(FinanceMentionMatch.Ambiguous, FinanceMentionResolver.Account("Revolut", [NewAccount("Revolut"), NewAccount("Revolut")]).Match);
        Assert.Equal(FinanceMentionMatch.Ambiguous,
            FinanceMentionResolver.Account("Revolut", [NewAccount("Revolut EUR"), NewAccount("Revolut USD")]).Match);
    }

    [Fact]
    public void AnExactName_WinsOverPartialMatches_AlsoWhenItStartsWithALeadWord()
    {
        var exact = NewAccount("Conto Revolut");
        var result = FinanceMentionResolver.Account("conto Revolut", [NewAccount("Revolut"), exact]);

        Assert.Equal((FinanceMentionMatch.Resolved, exact), (result.Match, result.Value));
    }

    [Fact]
    public void AnUnknownMention_IsNotFound_EvenWhenTheUserHasASingleAccount()
    {
        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Account("Fineco", [NewAccount("Revolut")]).Match);
        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Account("conto", [NewAccount("Revolut")]).Match);
        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Account("Revolut", []).Match);
    }

    // ---- Categories ----

    [Fact]
    public void ACategory_IsResolvedOnlyAmongTheCompatibleOnes()
    {
        var food = NewCategory("Cibo", CategoryType.Expense);
        var foodIncome = NewCategory("Cibo", CategoryType.Income);

        Assert.Equal(food, FinanceMentionResolver.Category("cibo", TransactionType.Expense, [food, foodIncome], allowPartial: true).Value);
        Assert.Equal(foodIncome, FinanceMentionResolver.Category("cibo", TransactionType.Income, [food, foodIncome], allowPartial: true).Value);
        // While the type is unknown, both match.
        Assert.Equal(FinanceMentionMatch.Ambiguous, FinanceMentionResolver.Category("cibo", null, [food, foodIncome], allowPartial: true).Match);
    }

    [Fact]
    public void AModelSuggestion_MustNameACategoryExactly_WhileTheUsersMentionMayBePartial()
    {
        var groceries = NewCategory("Spesa alimentare", CategoryType.Expense);

        Assert.Equal(groceries, FinanceMentionResolver.Category("spesa", TransactionType.Expense, [groceries], allowPartial: true).Value);
        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Category("spesa", TransactionType.Expense, [groceries], allowPartial: false).Match);
        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Category("Viaggi", TransactionType.Expense, [groceries], allowPartial: true).Match);
    }

    // AI-007 F3-B2 live run 1 diagnosis: the descriptions gpt-6-luna returned as stated categories, against the
    // synthetic category names of assistant_finance v1. Not found → asked again, never applied; a stated word
    // that is a category name resolves as the user's choice.
    [Fact]
    public void TheLiveRunsStatedDescriptions_AreNotFound_AmongTheDatasetCategories()
    {
        Category[] expense = [.. new[] { "Ristoranti", "Spesa", "Trasporti", "Casa", "Svago", "Salute", "Regali" }
            .Select(name => NewCategory(name, CategoryType.Expense))];
        Category[] income = [.. new[] { "Stipendio", "Rimborsi", "Vendite" }.Select(name => NewCategory(name, CategoryType.Income))];
        Category[] all = [.. expense, .. income];

        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Category("visita medica", TransactionType.Expense, all, allowPartial: true).Match);
        Assert.Equal(FinanceMentionMatch.NotFound, FinanceMentionResolver.Category("rimborso spese", TransactionType.Income, all, allowPartial: true).Match);
        Assert.Equal(expense[1], FinanceMentionResolver.Category("spesa", TransactionType.Expense, all, allowPartial: true).Value);
    }

    // ---- Dates ----

    // Sunday 2026-10-25, 11:00 in Rome (the day DST ends).
    private static readonly DateTimeOffset Sunday = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<AssistantDateField, DateOnly> Dates => new()
    {
        { Date(AssistantDateKind.Today, "oggi"), new DateOnly(2026, 10, 25) },
        { Date(AssistantDateKind.Yesterday, "ieri"), new DateOnly(2026, 10, 24) },
        { Date(AssistantDateKind.DayBeforeYesterday, "l'altro ieri"), new DateOnly(2026, 10, 23) },
        { Date(AssistantDateKind.DaysAgo, "tre giorni fa", daysAgo: 3), new DateOnly(2026, 10, 22) },
        { Date(AssistantDateKind.Weekday, "lunedì scorso", weekday: DayOfWeek.Monday), new DateOnly(2026, 10, 19) },
        // On a Sunday, "domenica scorsa" is a week ago, never today.
        { Date(AssistantDateKind.Weekday, "domenica scorsa", weekday: DayOfWeek.Sunday), new DateOnly(2026, 10, 18) },
        { Date(AssistantDateKind.Calendar, "il 3 ottobre", day: 3, month: 10), new DateOnly(2026, 10, 3) },
        { Date(AssistantDateKind.Calendar, "il 3/1/2025", day: 3, month: 1, year: 2025), new DateOnly(2025, 1, 3) },
        // Explicit future dates are kept (the draft flags them).
        { Date(AssistantDateKind.Calendar, "il 30 ottobre", day: 30, month: 10), new DateOnly(2026, 10, 30) }
    };

    [Theory]
    [MemberData(nameof(Dates))]
    public void DateExpressions_AreResolvedOnTheUsersLocalCalendar(AssistantDateField field, DateOnly expected)
    {
        Assert.Equal((expected, (FinanceDateProblem?)null), FinanceDateExpressionResolver.Resolve(field, "Europe/Rome", Sunday));
    }

    [Fact]
    public void TheLocalCalendar_DecidesTodayAndYesterday_NotUtc()
    {
        // 23:30Z on the 24th is already the 25th in Rome (and still the 24th in New York).
        var lateUtc = new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero);
        var yesterday = Date(AssistantDateKind.Yesterday, "ieri");

        Assert.Equal(new DateOnly(2026, 10, 24), FinanceDateExpressionResolver.Resolve(yesterday, "Europe/Rome", lateUtc).Date);
        Assert.Equal(new DateOnly(2026, 10, 23), FinanceDateExpressionResolver.Resolve(yesterday, "America/New_York", lateUtc).Date);
    }

    [Fact]
    public void ANonexistentDate_OrAMissingTimeZone_IsAProblem_NeverAGuess()
    {
        Assert.Equal(((DateOnly?)null, FinanceDateProblem.InvalidDate),
            FinanceDateExpressionResolver.Resolve(Date(AssistantDateKind.Calendar, "31 febbraio", day: 31, month: 2), "Europe/Rome", Sunday));
        Assert.Equal(((DateOnly?)null, FinanceDateProblem.TimeZoneRequired),
            FinanceDateExpressionResolver.Resolve(Date(AssistantDateKind.Yesterday, "ieri"), null, Sunday));
    }

    private static Account NewAccount(string name, string currency = "EUR") => Account.Create(UserId, name, AccountType.BankAccount, currency, Created);

    private static Category NewCategory(string name, CategoryType type) => Category.Create(UserId, name, type, null, Created);
}
