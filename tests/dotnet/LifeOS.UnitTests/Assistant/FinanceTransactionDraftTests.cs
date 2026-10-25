using LifeOS.Application.Finance.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.UnitTests.Assistant;

// AI-007 F2: the Finance draft's field rules, completeness, and the backend's resolution of when the
// transaction happened (defaults, the 12:00 convention, the user's time zone, DST).
public class FinanceTransactionDraftTests
{
    private const string Rome = "Europe/Rome";

    // 2026-10-24 11:00 in Rome (CEST, +02:00).
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    // ---- Fields and completeness ----

    [Fact]
    public void AnEmptyDraft_MissesEveryRequiredField_ACompleteOneNone()
    {
        Assert.Equal(["type", "amount", "accountId", "categoryId", "occurredAt"], FinanceTransactionDraft.Empty.MissingFields);
        Assert.False(FinanceTransactionDraft.Empty.IsComplete);

        var complete = Build(new FinanceDraftInput(TransactionType.Expense, 5m, null, Guid.CreateVersion7(), Guid.CreateVersion7(), "x"));
        Assert.Empty(complete.MissingFields);
        Assert.True(complete.IsComplete);

        // The note is optional; without a time zone the date is never resolved.
        var withoutZone = FinanceDraftBuilder.FromInput(new FinanceDraftInput(TransactionType.Income, 5m, null, Guid.CreateVersion7(), Guid.CreateVersion7()),
            null, Now);
        Assert.Equal(["occurredAt"], withoutZone.MissingFields);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.00001")]
    [InlineData("1000000000000000")]
    public void InvalidAmounts_AreRefused(string amount)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => Build(new FinanceDraftInput(Amount: decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(FinanceDraftFields.Amount, exception.ParamName);
    }

    [Fact]
    public void AmountLimits_AreTheTransactionsOwn()
    {
        Assert.Equal(Transaction.MaxAmount, Build(new FinanceDraftInput(Amount: Transaction.MaxAmount)).Amount);
        Assert.Equal(0.0001m, Build(new FinanceDraftInput(Amount: 0.0001m)).Amount);
    }

    [Fact]
    public void ATransfer_IsNotSupported()
    {
        var exception = Assert.Throws<ArgumentException>(() => Build(new FinanceDraftInput(TransactionType.Transfer)));
        Assert.Equal(FinanceDraftFields.Type, exception.ParamName);
    }

    [Fact]
    public void TheNote_IsTrimmed_KeepsNames_AndIsBounded()
    {
        Assert.Equal("colazione con Pamela", Build(new FinanceDraftInput(Note: "  colazione con Pamela \n")).Note);
        Assert.Null(Build(new FinanceDraftInput(Note: "   ")).Note);
        Assert.Equal(1000, Build(new FinanceDraftInput(Note: new string('a', 1000))).Note!.Length);
        Assert.Equal(FinanceDraftFields.Note, Assert.Throws<ArgumentException>(() => Build(new FinanceDraftInput(Note: new string('a', 1001)))).ParamName);
        Assert.Equal(FinanceDraftFields.Note, Assert.Throws<ArgumentException>(() => Build(new FinanceDraftInput(Note: "a\0b"))).ParamName);
    }

    [Fact]
    public void TheStatedCurrency_IsACode_AndNeverADefault()
    {
        Assert.Equal("USD", Build(new FinanceDraftInput(Currency: " usd ")).CurrencyMention);
        Assert.Null(Build(new FinanceDraftInput()).CurrencyMention);
        Assert.Equal(FinanceDraftFields.Currency, Assert.Throws<ArgumentException>(() => Build(new FinanceDraftInput(Currency: "EURO"))).ParamName);
    }

    [Fact]
    public void ACategory_CarriesItsSource_AndIdsMustBeReal()
    {
        var category = Guid.CreateVersion7();
        Assert.Equal(FinanceDraftCategorySource.User, Build(new FinanceDraftInput(CategoryId: category)).CategorySource);
        Assert.Equal(FinanceDraftCategorySource.Suggested,
            Build(new FinanceDraftInput(CategoryId: category, CategorySource: FinanceDraftCategorySource.Suggested)).CategorySource);
        Assert.Null(Build(new FinanceDraftInput(CategorySource: FinanceDraftCategorySource.Suggested)).CategorySource);

        Assert.Equal(FinanceDraftFields.Account, Assert.Throws<ArgumentException>(() => Build(new FinanceDraftInput(AccountId: Guid.Empty))).ParamName);
        Assert.Equal(FinanceDraftFields.Category, Assert.Throws<ArgumentException>(() => Build(new FinanceDraftInput(CategoryId: Guid.Empty))).ParamName);
    }

    [Fact]
    public void DatesOutsideTheRange_AndSubSecondTimes_AreRefused()
    {
        Assert.Equal(FinanceDraftFields.OccurredOn,
            Assert.ThrowsAny<ArgumentException>(() => Build(new FinanceDraftInput(OccurredOn: new DateOnly(1899, 12, 31)))).ParamName);
        Assert.Equal(FinanceDraftFields.OccurredOn,
            Assert.ThrowsAny<ArgumentException>(() => Build(new FinanceDraftInput(OccurredOn: new DateOnly(3000, 1, 1)))).ParamName);
        Assert.Equal(FinanceDraftFields.OccurredTime,
            Assert.Throws<ArgumentException>(() => Build(new FinanceDraftInput(OccurredTime: new TimeOnly(8, 0, 0, 500)))).ParamName);
    }

    [Fact]
    public void TheOccurrence_MustMatchTheRequestedDateAndTime()
    {
        var occurrence = FinanceDraftOccurrence.Create(new DateOnly(2026, 10, 20), FinanceDraftOccurrence.Noon, FinanceDraftDateSource.Explicit,
            FinanceDraftTimeSource.Noon, Rome, Now.AddDays(-4));

        Assert.Throws<ArgumentException>(() => FinanceTransactionDraft.Create(null, null, null, null, null, null, null, new DateOnly(2026, 10, 21), null, occurrence));
        Assert.Throws<ArgumentException>(() => FinanceTransactionDraft.Create(null, null, null, null, null, null, null, null, null, occurrence));
        Assert.Throws<ArgumentException>(() => FinanceDraftOccurrence.Create(new DateOnly(2026, 10, 20), new TimeOnly(13, 0), FinanceDraftDateSource.Explicit,
            FinanceDraftTimeSource.Noon, Rome, Now));
    }

    // ---- When it happened ----

    [Fact]
    public void NoDate_IsToday_AtTheCurrentTime_InTheUsersZone()
    {
        var occurrence = Resolve(null, null, Now.AddTicks(1234567));

        Assert.Equal((new DateOnly(2026, 10, 24), new TimeOnly(11, 0), FinanceDraftDateSource.Today, FinanceDraftTimeSource.CurrentTime, Rome),
            (occurrence.LocalDate, occurrence.LocalTime, occurrence.DateSource, occurrence.TimeSource, occurrence.TimeZoneId));
        Assert.Equal(Now, occurrence.OccurredAtUtc); // the instant itself, to the second
    }

    [Fact]
    public void Today_IsTheUsersLocalDate_NotTheUtcOne()
    {
        // 22:30Z on the 24th is already 00:30 on the 25th in Rome.
        var occurrence = Resolve(null, null, new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero));

        Assert.Equal((new DateOnly(2026, 10, 25), new TimeOnly(0, 30)), (occurrence.LocalDate, occurrence.LocalTime));
    }

    [Fact]
    public void APastDateWithoutTime_IsNoonLocal_AConventionMarkedAsSuch()
    {
        var occurrence = Resolve(new DateOnly(2026, 10, 23), null, Now);

        Assert.Equal((FinanceDraftDateSource.Explicit, FinanceDraftTimeSource.Noon, FinanceDraftOccurrence.Noon),
            (occurrence.DateSource, occurrence.TimeSource, occurrence.LocalTime));
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero), occurrence.OccurredAtUtc);
    }

    [Fact]
    public void ExplicitTodayWithoutTime_IsTheCurrentTime_AndAnExplicitTimeIsKept()
    {
        Assert.Equal(FinanceDraftTimeSource.CurrentTime, Resolve(new DateOnly(2026, 10, 24), null, Now).TimeSource);

        var explicitTime = Resolve(new DateOnly(2026, 10, 23), new TimeOnly(8, 15), Now);
        Assert.Equal((FinanceDraftTimeSource.Explicit, new DateTimeOffset(2026, 10, 23, 6, 15, 0, TimeSpan.Zero)),
            (explicitTime.TimeSource, explicitTime.OccurredAtUtc));
    }

    [Fact]
    public void AFutureInstant_ComesOnlyFromAnExplicitDateOrTime()
    {
        Assert.True(Resolve(new DateOnly(2026, 11, 2), null, Now).OccurredAtUtc > Now);
        Assert.True(Resolve(null, new TimeOnly(18, 0), Now).OccurredAtUtc > Now);
        Assert.True(Resolve(null, null, Now).OccurredAtUtc <= Now);
    }

    [Fact]
    public void DstGapsAndOverlaps_FollowTheLifeOSRules()
    {
        // 2026-03-29 02:30 does not exist in Rome: shifted forward by the gap (03:30 CEST = 01:30Z).
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), Resolve(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), Now).OccurredAtUtc);

        // 2026-10-25 02:30 happens twice: the earlier instant (CEST, 00:30Z).
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), Resolve(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), Now).OccurredAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus")]
    [InlineData("W. Europe Standard Time")] // a Windows id, not IANA
    public void WithoutAValidIanaZone_NothingIsResolved_AndNoZoneIsAssumed(string? zone)
    {
        Assert.Null(FinanceDraftOccurrenceResolver.Resolve(null, null, zone, Now));
        Assert.Null(FinanceDraftOccurrenceResolver.Resolve(new DateOnly(2026, 10, 23), new TimeOnly(8, 0), zone, Now));
    }

    // ---- Patches ----

    [Fact]
    public void APatch_ReplacesClearsOrKeepsEachField()
    {
        var account = Guid.CreateVersion7();
        var draft = Build(new FinanceDraftInput(TransactionType.Expense, 5m, "EUR", account, Guid.CreateVersion7(), "colazione"));

        var patched = FinanceDraftBuilder.Apply(draft, new FinanceDraftPatch(Amount: 7m, Clear: [FinanceDraftFields.Category, FinanceDraftFields.Currency]),
            Rome, Now.AddHours(1));

        Assert.Equal((7m, (Guid?)account, (Guid?)null, (FinanceDraftCategorySource?)null, (string?)null, "colazione"),
            (patched.Amount, patched.AccountId, patched.CategoryId, patched.CategorySource, patched.CurrencyMention, patched.Note));
        // The date was not touched: the occurrence is kept as the user saw it.
        Assert.Equal(draft.Occurrence, patched.Occurrence);
    }

    [Fact]
    public void ASuggestedCategory_NeverReplacesTheUsersChoice()
    {
        var chosen = Guid.CreateVersion7();
        var suggested = Guid.CreateVersion7();
        var draft = Build(new FinanceDraftInput(TransactionType.Expense, 5m, CategoryId: chosen));

        // Another category, or the same one: the user's choice and its source stay, so the patch is a no-op.
        foreach (var category in new[] { suggested, chosen })
        {
            Assert.Equal(draft, FinanceDraftBuilder.Apply(draft,
                new FinanceDraftPatch(CategoryId: category, CategorySource: FinanceDraftCategorySource.Suggested), Rome, Now));
        }

        // The rest of the same patch still applies.
        var patched = FinanceDraftBuilder.Apply(draft,
            new FinanceDraftPatch(Amount: 7m, CategoryId: suggested, CategorySource: FinanceDraftCategorySource.Suggested), Rome, Now);
        Assert.Equal((7m, (Guid?)chosen, (FinanceDraftCategorySource?)FinanceDraftCategorySource.User),
            (patched.Amount, patched.CategoryId, patched.CategorySource));

        // Without a user choice a suggestion applies, and the user's own choice then replaces it.
        var withSuggestion = FinanceDraftBuilder.Apply(FinanceDraftBuilder.Apply(draft, new FinanceDraftPatch(Clear: [FinanceDraftFields.Category]), Rome, Now),
            new FinanceDraftPatch(CategoryId: suggested, CategorySource: FinanceDraftCategorySource.Suggested), Rome, Now);
        Assert.Equal(((Guid?)suggested, (FinanceDraftCategorySource?)FinanceDraftCategorySource.Suggested), (withSuggestion.CategoryId, withSuggestion.CategorySource));

        var userChoice = FinanceDraftBuilder.Apply(withSuggestion, new FinanceDraftPatch(CategoryId: chosen), Rome, Now);
        Assert.Equal(((Guid?)chosen, (FinanceDraftCategorySource?)FinanceDraftCategorySource.User), (userChoice.CategoryId, userChoice.CategorySource));
    }

    [Fact]
    public void RepeatingTheSameDate_KeepsTheOccurrence_ChangingItResolvesAgain()
    {
        var draft = Build(new FinanceDraftInput(OccurredOn: new DateOnly(2026, 10, 23)));

        Assert.Equal(draft, FinanceDraftBuilder.Apply(draft, new FinanceDraftPatch(OccurredOn: new DateOnly(2026, 10, 23)), Rome, Now.AddHours(2)));

        var cleared = FinanceDraftBuilder.Apply(draft, new FinanceDraftPatch(Clear: [FinanceDraftFields.OccurredOn]), Rome, Now.AddHours(2));
        Assert.Equal((FinanceDraftDateSource.Today, FinanceDraftTimeSource.CurrentTime, Now.AddHours(2)),
            (cleared.Occurrence!.DateSource, cleared.Occurrence.TimeSource, cleared.Occurrence.OccurredAtUtc));
    }

    [Fact]
    public void AnUnresolvedDate_IsResolvedOnceTheUserHasAZone()
    {
        var draft = FinanceDraftBuilder.FromInput(new FinanceDraftInput(Amount: 5m), null, Now);
        Assert.Null(draft.Occurrence);

        var patched = FinanceDraftBuilder.Apply(draft, new FinanceDraftPatch(Note: "pranzo"), Rome, Now);
        Assert.Equal(FinanceDraftDateSource.Today, patched.Occurrence!.DateSource);
    }

    private static FinanceTransactionDraft Build(FinanceDraftInput input) => FinanceDraftBuilder.FromInput(input, Rome, Now);

    private static FinanceDraftOccurrence Resolve(DateOnly? date, TimeOnly? time, DateTimeOffset now) =>
        FinanceDraftOccurrenceResolver.Resolve(date, time, Rome, now)!;
}
