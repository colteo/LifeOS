using LifeOS.Application.Automation;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Users.SetTimeZone;
using LifeOS.Domain.Finance.Assistant;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Application.Finance.Assistant;

// When the transaction happened, resolved on the backend (never by a model) from the user's explicit
// local date/time, the user's time zone and the current instant (AI-007 F2 B):
//   - no date: today in the user's zone;
//   - today without a time: the current time (the instant itself, to the second);
//   - any other date without a time: 12:00 local, a convention shown as such and replaceable;
//   - an explicit time is kept; an explicit date may lie in the future (the view flags it);
//   - local → UTC with the LifeOS DST rules (LocalSchedule.ToUtc, AUTO-001 §6): a time inside a
//     spring-forward gap moves forward by the gap, an ambiguous fall-back time is the earlier instant.
// Without a valid IANA time zone nothing is resolved (null): no zone is ever assumed.
public static class FinanceDraftOccurrenceResolver
{
    public static FinanceDraftOccurrence? Resolve(DateOnly? requestedDate, TimeOnly? requestedTime, string? timeZoneId, DateTimeOffset now)
    {
        if (!SetTimeZoneHandler.TryNormalizeIanaTimeZone(timeZoneId, out var zoneId))
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var nowUtc = now.ToUniversalTime();
        var currentInstant = nowUtc.AddTicks(-(nowUtc.Ticks % TimeSpan.TicksPerSecond));
        var localNow = TimeZoneInfo.ConvertTime(currentInstant, zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);

        var date = requestedDate ?? today;
        var dateSource = requestedDate is null ? FinanceDraftDateSource.Today : FinanceDraftDateSource.Explicit;

        if (requestedTime is { } time)
        {
            return FinanceDraftOccurrence.Create(date, time, dateSource, FinanceDraftTimeSource.Explicit, zoneId,
                LocalSchedule.ToUtc(zone, date.ToDateTime(time)));
        }

        if (date == today)
        {
            return FinanceDraftOccurrence.Create(date, TimeOnly.FromDateTime(localNow.DateTime), dateSource, FinanceDraftTimeSource.CurrentTime,
                zoneId, currentInstant);
        }

        return FinanceDraftOccurrence.Create(date, FinanceDraftOccurrence.Noon, dateSource, FinanceDraftTimeSource.Noon, zoneId,
            LocalSchedule.ToUtc(zone, date.ToDateTime(FinanceDraftOccurrence.Noon)));
    }
}

// Builds drafts from structured input. Throws ArgumentException (ParamName = the field) for a value the
// draft cannot hold.
public static class FinanceDraftBuilder
{
    public static FinanceTransactionDraft FromInput(FinanceDraftInput input, string? timeZoneId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = input.CategoryId is null ? (FinanceDraftCategorySource?)null : input.CategorySource;

        // Shape first (so the date is in range before it is resolved), then with the resolved occurrence.
        FinanceTransactionDraft.Create(input.Type, input.Amount, input.Currency, input.AccountId, input.CategoryId, source, input.Note,
            input.OccurredOn, input.OccurredTime, occurrence: null);

        return FinanceTransactionDraft.Create(input.Type, input.Amount, input.Currency, input.AccountId, input.CategoryId, source, input.Note,
            input.OccurredOn, input.OccurredTime, FinanceDraftOccurrenceResolver.Resolve(input.OccurredOn, input.OccurredTime, timeZoneId, now));
    }

    // The draft after the patch. The occurrence is resolved again only when the requested date or time
    // changes, or while it is unresolved (e.g. the user has set a time zone since): re-sending the same
    // values keeps it, so such a patch changes nothing. A suggested category never replaces (nor downgrades)
    // one the user chose: it is ignored, and applies only to a draft without a user category.
    public static FinanceTransactionDraft Apply(FinanceTransactionDraft current, FinanceDraftPatch patch, string? timeZoneId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);
        var clear = patch.Clear ?? [];
        var suggestedCategory = patch.CategoryId is not null && patch.CategorySource == FinanceDraftCategorySource.Suggested;
        var keepUserCategory = suggestedCategory && current.CategorySource == FinanceDraftCategorySource.User
            && !clear.Contains(FinanceDraftFields.Category);

        var type = Pick(clear, FinanceDraftFields.Type, patch.Type, current.Type);
        var amount = Pick(clear, FinanceDraftFields.Amount, patch.Amount, current.Amount);
        var accountId = Pick(clear, FinanceDraftFields.Account, patch.AccountId, current.AccountId);
        var categoryId = keepUserCategory ? current.CategoryId : Pick(clear, FinanceDraftFields.Category, patch.CategoryId, current.CategoryId);
        var date = Pick(clear, FinanceDraftFields.OccurredOn, patch.OccurredOn, current.RequestedDate);
        var time = Pick(clear, FinanceDraftFields.OccurredTime, patch.OccurredTime, current.RequestedTime);
        var currency = clear.Contains(FinanceDraftFields.Currency) ? null : patch.Currency ?? current.CurrencyMention;
        var note = clear.Contains(FinanceDraftFields.Note) ? null : patch.Note ?? current.Note;
        var source = categoryId is null ? (FinanceDraftCategorySource?)null
            : patch.CategoryId is not null && !keepUserCategory ? patch.CategorySource : current.CategorySource;

        FinanceTransactionDraft.Create(type, amount, currency, accountId, categoryId, source, note, date, time, occurrence: null);

        var occurrence = date == current.RequestedDate && time == current.RequestedTime && current.Occurrence is not null
            ? current.Occurrence
            : FinanceDraftOccurrenceResolver.Resolve(date, time, timeZoneId, now);

        return FinanceTransactionDraft.Create(type, amount, currency, accountId, categoryId, source, note, date, time, occurrence);
    }

    private static T? Pick<T>(IReadOnlyCollection<string> clear, string field, T? value, T? current) where T : struct =>
        clear.Contains(field) ? null : value ?? current;
}

// The draft's references checked against the user's Finance data, user-scoped (another user's account or
// category is reported exactly like a missing one). CreateTransactionHandler checks existence, ownership and
// category compatibility again inside the confirmation; the currency rule is the assistant's own (the
// handler does not know which currency the user stated).
public sealed class FinanceDraftReferences(IAccountRepository accounts, ICategoryRepository categories)
{
    // The first problem that prevents accepting the draft (FinanceOperationErrors code and field), if any.
    public async Task<(string Code, string Field)?> FirstProblemAsync(Guid userId, FinanceTransactionDraft draft, CancellationToken cancellationToken)
    {
        var (_, _, problems) = await InspectAsync(userId, draft, cancellationToken);

        return problems.Count == 0 ? null : problems[0];
    }

    // The currency rule only: null, or currency_mismatch (an account that no longer exists is left to
    // CreateTransactionHandler, which reports it).
    public async Task<string?> CurrencyProblemAsync(Guid userId, FinanceTransactionDraft draft, CancellationToken cancellationToken) =>
        draft is { AccountId: { } accountId, CurrencyMention: { } currency }
        && await accounts.GetByIdAsync(userId, accountId, cancellationToken) is { } account
        && account.Currency != currency
            ? FinanceOperationErrors.CurrencyMismatch
            : null;

    public async Task<FinanceOperationView> ViewAsync(FinanceOperation operation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var draft = operation.Draft;
        var (account, category, problems) = await InspectAsync(operation.UserId, draft, cancellationToken);
        var issues = new List<string>();

        if (operation.IsOpen)
        {
            issues.AddRange(problems.Select(problem => problem.Code));

            if (draft.Occurrence is null)
            {
                issues.Add(FinanceOperationErrors.TimeZoneRequired);
            }
        }

        var canConfirm = operation.Status == Domain.Assistant.AssistantOperationStatus.AwaitingConfirmation
            && issues.Count == 0
            && !operation.IsExpiredAt(now);

        return new FinanceOperationView(operation, account, category, issues, draft.Occurrence?.OccurredAtUtc > now, canConfirm);
    }

    private async Task<(FinanceDraftAccountView?, FinanceDraftCategoryView?, List<(string Code, string Field)>)> InspectAsync(
        Guid userId,
        FinanceTransactionDraft draft,
        CancellationToken cancellationToken)
    {
        var problems = new List<(string Code, string Field)>();
        FinanceDraftAccountView? accountView = null;
        FinanceDraftCategoryView? categoryView = null;

        if (draft.AccountId is { } accountId)
        {
            var account = await accounts.GetByIdAsync(userId, accountId, cancellationToken);
            accountView = new FinanceDraftAccountView(accountId, account?.Name, account?.Currency, account?.AccountType);

            if (account is null)
            {
                problems.Add((FinanceOperationErrors.AccountNotFound, FinanceDraftFields.Account));
            }
            else if (draft.CurrencyMention is { } currency && account.Currency != currency)
            {
                problems.Add((FinanceOperationErrors.CurrencyMismatch, FinanceDraftFields.Account));
            }
        }

        if (draft.CategoryId is { } categoryId)
        {
            var category = await categories.GetByIdAsync(userId, categoryId, cancellationToken);
            categoryView = new FinanceDraftCategoryView(categoryId, category?.Name, category?.CategoryType, category?.ParentCategoryId,
                draft.CategorySource!.Value);

            if (category is null)
            {
                problems.Add((FinanceOperationErrors.CategoryNotFound, FinanceDraftFields.Category));
            }
            else if (draft.Type is { } type && TransactionInputResolver.CategoryCompatibilityError(type, category.CategoryType) is not null)
            {
                problems.Add((FinanceOperationErrors.CategoryIncompatible, FinanceDraftFields.Category));
            }
        }

        return (accountView, categoryView, problems);
    }
}
