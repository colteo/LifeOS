using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LifeOS.Application.Automation;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Application.ActionAgent;

// A local calendar month.
public readonly record struct AgentMonth(int Year, int Month)
{
    public static AgentMonth Of(DateOnly date) => new(date.Year, date.Month);

    public AgentMonth Next() => Month == 12 ? new(Year + 1, 1) : new(Year, Month + 1);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");
}

// What one run may touch: its owner, the review it analyses, the review's zone (the zone its week was
// resolved with), the months whose budget may be read, and the months an adjustment may target.
public sealed record AgentRunScope(
    Guid UserId,
    WeeklyReview Review,
    TimeZoneInfo Zone,
    DateTimeOffset NowUtc,
    IReadOnlyList<AgentMonth> ReadableMonths,
    IReadOnlyList<AgentMonth> TargetMonths);

// The budget LifeOS itself read through get_budget_status (never model output). Amount is null when
// no budget is set for that month and currency.
public sealed record BudgetObservation(AgentMonth Month, string Currency, decimal? Amount);

// The result of executing one tool call: the JSON returned to the model, and the budget it observed.
public sealed record AgentToolOutcome(JsonElement Result, BudgetObservation? Observation = null, bool InvalidArguments = false);

// AI-002: the agent's read-only tools, defined and executed by LifeOS. Read-only by construction: the
// only dependencies are the weekly review (already loaded, user-scoped) and the existing budget QUERY
// handler; nothing here can write. Inputs are validated strictly and bounded to the run's scope; outputs
// are minimal, contain no ids, and are bounded in size.
public sealed class ActionAgentTools(GetMonthlyBudgetHandler budgetStatus)
{
    public const string ToolSchemaVersion = "action-agent-tools-v1";

    public const string GetWeeklyReview = "get_weekly_review";
    public const string GetBudgetStatus = "get_budget_status";

    internal const int MaxCurrencies = 10;
    internal const int MaxCategories = 5;
    internal const int MaxNameLength = 100;

    // snake_case like the AI service; absent values are omitted rather than sent as null.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<AgentToolDefinition> Definitions { get; } =
    [
        new(GetWeeklyReview,
            "Returns the finance section of the saved weekly review being analysed: the week's dates and, per currency, "
            + "expenses, income, net flow and the largest expense categories. Takes no arguments.",
            Schema("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")),
        new(GetBudgetStatus,
            "Returns the monthly budget for one month and currency: whether one is set, its amount, spent so far, remaining, "
            + "expected recurring and planned expenses, free to spend and remaining days. Read-only.",
            Schema("""
                {"type":"object","properties":{
                  "year":{"type":"integer","description":"Calendar year, e.g. 2026."},
                  "month":{"type":"integer","description":"Calendar month 1-12."},
                  "currency":{"type":"string","description":"3-letter currency code, e.g. EUR."}},
                 "required":["year","month","currency"],"additionalProperties":false}
                """))
    ];

    public static bool IsTool(string name) => Definitions.Any(definition => definition.Name == name);

    // Identifies a call by its meaning, not its spelling: the same tool with the same parsed arguments is
    // the same call (repeated-call detection). Invalid arguments are keyed by their raw text.
    public static string CallKey(string tool, JsonElement arguments) => tool switch
    {
        GetWeeklyReview when IsEmptyObject(arguments) => GetWeeklyReview,
        GetBudgetStatus when TryReadBudgetArguments(arguments, out var month, out var currency) => $"{GetBudgetStatus}:{month}:{currency}",
        _ => $"{tool}:invalid:{arguments.GetRawText()}"
    };

    public async Task<AgentToolOutcome> ExecuteAsync(AgentRunScope scope, string tool, JsonElement arguments, CancellationToken cancellationToken) =>
        tool switch
        {
            GetWeeklyReview => WeeklyReview(scope, arguments),
            GetBudgetStatus => await BudgetStatusAsync(scope, arguments, cancellationToken),
            _ => throw new ArgumentException("Unknown tool.", nameof(tool))
        };

    private static AgentToolOutcome WeeklyReview(AgentRunScope scope, JsonElement arguments)
    {
        if (!IsEmptyObject(arguments))
        {
            return Invalid("get_weekly_review takes no arguments.");
        }

        var review = scope.Review;
        var result = new WeeklyReviewToolResult(
            review.WeekStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            review.WeekEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            review.Snapshot.Finance.Currencies
                .Take(MaxCurrencies)
                .Select(currency => new CurrencyToolResult(
                    currency.Currency,
                    currency.Expenses,
                    currency.Income,
                    currency.NetFlow,
                    currency.ExpenseCategories.Take(MaxCategories).Select(category => new CategoryToolResult(Name(category.Name), category.Amount)).ToList()))
                .ToList());

        return new(JsonSerializer.SerializeToElement(result, Json));
    }

    private async Task<AgentToolOutcome> BudgetStatusAsync(AgentRunScope scope, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryReadBudgetArguments(arguments, out var month, out var currency))
        {
            return Invalid("Arguments must be an object with integer year and month and a 3-letter currency code.");
        }

        if (!scope.ReadableMonths.Contains(month))
        {
            return Invalid($"The month must be one of: {string.Join(", ", scope.ReadableMonths)}.");
        }

        // The local month as a half-open UTC range in the review's zone (the budget query's contract).
        var first = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var fromUtc = LocalSchedule.ToUtc(scope.Zone, first);
        var toUtc = LocalSchedule.ToUtc(scope.Zone, first.AddMonths(1));
        var offset = (int)scope.Zone.GetUtcOffset(scope.NowUtc).TotalMinutes;

        var read = await budgetStatus.HandleAsync(scope.UserId,
            new GetMonthlyBudgetQuery(month.Year, month.Month, currency, fromUtc, toUtc, offset), cancellationToken);

        if (read.Status != MonthlyBudgetStatus.Ok)
        {
            return Invalid("The budget for that month cannot be read.");
        }

        var budget = read.Budget;
        var result = budget is null
            ? new BudgetToolResult(month.Year, month.Month, currency, false)
            : new BudgetToolResult(month.Year, month.Month, currency, true, budget.Amount, budget.Spent, budget.Remaining,
                budget.ExpectedRecurringExpenses, budget.ExpectedPlannedExpenses, budget.FreeToSpend, budget.RemainingDays);

        return new(JsonSerializer.SerializeToElement(result, Json), new BudgetObservation(month, currency, budget?.Amount));
    }

    // Exactly {year, month, currency}: integers and a valid currency code, nothing else.
    private static bool TryReadBudgetArguments(JsonElement arguments, out AgentMonth month, out string currency)
    {
        month = default;
        currency = "";

        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(["currency", "month", "year"]) is false
            || arguments.GetProperty("year") is not { ValueKind: JsonValueKind.Number } yearValue || !yearValue.TryGetInt32(out var year)
            || arguments.GetProperty("month") is not { ValueKind: JsonValueKind.Number } monthValue || !monthValue.TryGetInt32(out var monthNumber)
            || arguments.GetProperty("currency") is not { ValueKind: JsonValueKind.String } currencyValue)
        {
            return false;
        }

        try
        {
            MonthlyBudget.ValidateMonth(year, monthNumber);
            currency = MonthlyBudget.NormalizeCurrency(currencyValue.GetString()!);
        }
        catch (ArgumentException)
        {
            return false;
        }

        month = new AgentMonth(year, monthNumber);
        return true;
    }

    private static AgentToolOutcome Invalid(string message) =>
        new(JsonSerializer.SerializeToElement(new ToolError("invalid_arguments", message), Json), InvalidArguments: true);

    private static bool IsEmptyObject(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && !arguments.EnumerateObject().Any();

    private static string Name(string name)
    {
        var trimmed = name.Trim();

        return trimmed.Length <= MaxNameLength ? trimmed : trimmed[..MaxNameLength].TrimEnd();
    }

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private sealed record WeeklyReviewToolResult(string WeekStart, string WeekEnd, List<CurrencyToolResult> Currencies);

    private sealed record CurrencyToolResult(string Currency, decimal Expenses, decimal Income, decimal NetFlow, List<CategoryToolResult> TopExpenseCategories);

    private sealed record CategoryToolResult(string Name, decimal Amount);

    private sealed record BudgetToolResult(
        int Year,
        int Month,
        string Currency,
        bool BudgetSet,
        decimal? Amount = null,
        decimal? Spent = null,
        decimal? Remaining = null,
        decimal? ExpectedRecurringExpenses = null,
        decimal? ExpectedPlannedExpenses = null,
        decimal? FreeToSpend = null,
        int? RemainingDays = null);

    private sealed record ToolError(string Error, string Message);
}
