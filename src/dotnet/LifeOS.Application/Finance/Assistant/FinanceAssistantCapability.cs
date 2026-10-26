using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Users;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Assistant;

// AI-007 F3: the Finance capability of the turn processor. It turns a validated interpretation into a draft
// input or patch and applies it ONLY through the F2 use cases (Create/Update…Handler: same validation, same
// expiry, same versions as the HTTP API). It never confirms, never cancels, never writes Finance and never
// touches the operation repository: a transaction is recorded only by POST …/confirm.
//
// What the model said is never used as it is:
//   - account: a mention, resolved among the user's accounts (FinanceMentionResolver) to one id, or asked
//     again (ambiguous / not found); never preselected, also when the user has one account;
//   - category: a stated name → the user's choice (source User) only as FinanceMentionResolver.StatedCategory
//     allows (part of a name needs the word "categoria", F3-B2), else asked; a model proposal → Suggested, only
//     when it names exactly one compatible category; a suggestion never replaces the user's choice (F2 builder);
//     a mention that cannot be applied is asked, never moved to the note;
//   - amount: the protocol's decimal, within Finance's limits, else asked again;
//   - currency: only what the user stated; an account in another currency is not applied (asked again);
//   - date: a date expression resolved on the backend in the user's time zone (FinanceDateExpressionResolver);
//     the time of day follows the F2 conventions (today → now, another day → 12:00);
//   - fields the model marked ambiguous are asked, not applied.
// A field that cannot be applied does not block the others; the reply asks about the first problem.
//
// Fencing: ApplyAsync runs inside the turn's unit of work after the turn lock, and only if the open operation
// is still the one the interpretation was made for (same id and draft version); otherwise draft_changed and
// nothing is written. The patch itself is conditional on that version (UpdateFinanceDraftHandler).
public sealed class FinanceAssistantCapability(
    GetActiveFinanceOperationHandler activeOperation,
    CreateFinanceOperationHandler createOperation,
    UpdateFinanceDraftHandler updateDraft,
    IAccountRepository accounts,
    ICategoryRepository categories,
    IUserRepository users,
    TimeProvider clock) : IAssistantCapability
{
    // Category names sent to the interpreter, per type (AI-007 D-6/D-7: bounded).
    public const int MaxCategoryNames = AssistantInterpretationProtocol.MaxCategoryNames;

    public async Task<AssistantTurnPreparation> PrepareAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var open = (await activeOperation.HandleAsync(userId, conversationId, cancellationToken)).Value;
        var draft = open?.Operation.Draft;
        var all = await categories.GetAllAsync(userId, cancellationToken);

        var context = new AssistantInterpretationContext(
            AssistantInterpretationProtocol.Locale,
            new AssistantFinanceContext(
                open is null ? null : DraftContext(open),
                draft?.Type is TransactionType.Income ? [] : Names(all, CategoryType.Expense),
                draft?.Type is TransactionType.Expense ? [] : Names(all, CategoryType.Income)));

        return new AssistantTurnPreparation(context, open is null ? null : new AssistantOperationSnapshot(open.Operation.Id, open.Operation.DraftVersion));
    }

    public async Task<AssistantCapabilityOutcome> ApplyAsync(Guid userId, Guid conversationId, AssistantTurnPreparation preparation,
        AssistantInterpretation interpretation, CancellationToken cancellationToken)
    {
        var open = (await activeOperation.HandleAsync(userId, conversationId, cancellationToken)).Value;

        if ((open is null ? null : new AssistantOperationSnapshot(open.Operation.Id, open.Operation.DraftVersion)) != preparation.OpenOperation)
        {
            return AssistantCapabilityOutcome.Failed(AssistantErrors.DraftChanged);
        }

        switch (interpretation.Intent)
        {
            case AssistantIntent.GeneralQuestion:
                return AssistantCapabilityOutcome.Answered(FinanceAssistantReplies.GeneralQuestion);

            case AssistantIntent.Unsupported:
                return AssistantCapabilityOutcome.Answered(FinanceAssistantReplies.Unsupported(interpretation.UnsupportedReason!.Value));

            case AssistantIntent.Clarification:
                return AssistantCapabilityOutcome.Answered(FinanceAssistantReplies.NotUnderstoodWith(open));

            // Never a confirmation: only POST …/confirm records a transaction.
            case AssistantIntent.FinanceDraftConfirm:
                return AssistantCapabilityOutcome.Answered(FinanceAssistantReplies.ConfirmRequested(open));

            // Detected, not executed: a misread message must not discard a draft. POST …/cancel cancels.
            case AssistantIntent.FinanceDraftCancel:
                return AssistantCapabilityOutcome.Answered(open is null ? FinanceAssistantReplies.NothingToCancel : FinanceAssistantReplies.CancelWithButton);

            case AssistantIntent.FinanceExpense or AssistantIntent.FinanceIncome when open is not null:
                return AssistantCapabilityOutcome.Answered(FinanceAssistantReplies.AlreadyOpen);

            case AssistantIntent.FinanceDraftUpdate when open is null:
                return AssistantCapabilityOutcome.Answered(FinanceAssistantReplies.NothingToUpdate);
        }

        var plan = await PlanAsync(userId, interpretation, open, cancellationToken);

        var result = open is null
            ? await createOperation.HandleAsync(userId, conversationId, plan.ToInput(), cancellationToken)
            : await updateDraft.HandleAsync(userId, open.Operation.Id, open.Operation.DraftVersion, plan.ToPatch(), cancellationToken);

        return result.Status switch
        {
            AssistantResultStatus.Ok => AssistantCapabilityOutcome.Answered(FinanceAssistantReplies.AfterChange(result.Value!.View, plan.Issue)),

            // A reference the resolution accepted and the use case refused (nothing was changed): ask again.
            AssistantResultStatus.Rejected => AssistantCapabilityOutcome.Answered(
                FinanceAssistantReplies.Rejected(result.Code, plan.Type ?? open?.Operation.Draft.Type)),

            // A value the draft cannot hold got past the protocol checks.
            AssistantResultStatus.Invalid => AssistantCapabilityOutcome.Failed(AssistantErrors.AssistantInvalidOutput),

            // The draft moved: edited, confirmed, cancelled, expired, or another one was opened.
            _ => AssistantCapabilityOutcome.Failed(AssistantErrors.DraftChanged)
        };
    }

    // ---- Interpretation → draft change ----

    private async Task<FinanceTurnPlan> PlanAsync(Guid userId, AssistantInterpretation interpretation, FinanceOperationView? open,
        CancellationToken cancellationToken)
    {
        var fields = interpretation.Finance;
        var ambiguous = interpretation.AmbiguousFields;
        var draft = open?.Operation.Draft ?? FinanceTransactionDraft.Empty;
        var plan = new FinanceTurnPlan();

        bool Usable(string name, AssistantFieldState state)
        {
            if (state == AssistantFieldState.Absent)
            {
                return false;
            }

            if (ambiguous.Contains(name))
            {
                plan.Report(new FinanceTurnIssue(FinanceTurnIssues.FieldAmbiguous, name));
                return false;
            }

            return true;
        }

        plan.Type = interpretation.Intent switch
        {
            AssistantIntent.FinanceExpense => TransactionType.Expense,
            AssistantIntent.FinanceIncome => TransactionType.Income,
            _ when Usable(AssistantFinanceFieldNames.Type, fields.Type.State) => fields.Type.Value == "income" ? TransactionType.Income : TransactionType.Expense,
            _ => null
        };

        var type = plan.Type ?? draft.Type;

        if (Usable(AssistantFinanceFieldNames.Amount, fields.Amount.State))
        {
            if (AssistantInterpretationValidator.TryParseAmount(fields.Amount.Value, out var amount) && amount > 0 && amount <= Transaction.MaxAmount)
            {
                plan.Amount = amount;
            }
            else
            {
                plan.Report(new FinanceTurnIssue(FinanceTurnIssues.AmountInvalid));
            }
        }

        if (Usable(AssistantFinanceFieldNames.Currency, fields.Currency.State))
        {
            if (fields.Currency.State == AssistantFieldState.Clear)
            {
                plan.Clear.Add(FinanceDraftFields.Currency);
            }
            else
            {
                plan.Currency = fields.Currency.Value;
            }
        }

        var statedCurrency = plan.Clear.Contains(FinanceDraftFields.Currency) ? null : plan.Currency ?? draft.CurrencyMention;
        await PlanAccountAsync(userId, fields.Account, Usable(AssistantFinanceFieldNames.Account, fields.Account.State), statedCurrency, open, plan,
            cancellationToken);

        await PlanCategoryAsync(userId, fields.Category, Usable(AssistantFinanceFieldNames.Category, fields.Category.State), type, open, plan,
            cancellationToken);

        if (Usable(AssistantFinanceFieldNames.Note, fields.Note.State))
        {
            switch (fields.Note.State)
            {
                case AssistantFieldState.Clear:
                    plan.Clear.Add(FinanceDraftFields.Note);
                    break;

                // A proposed note fills an empty one; it never rewrites what the draft already says.
                case AssistantFieldState.Inferred when draft.Note is not null:
                    break;

                default:
                    plan.Note = fields.Note.Value;
                    break;
            }
        }

        if (Usable(AssistantFinanceFieldNames.Date, fields.Date.State))
        {
            if (fields.Date.State == AssistantFieldState.Clear)
            {
                plan.Clear.Add(FinanceDraftFields.OccurredOn);
            }
            else
            {
                var timeZoneId = (await users.GetByIdAsync(userId, cancellationToken))?.TimeZoneId;
                var (date, problem) = FinanceDateExpressionResolver.Resolve(fields.Date, timeZoneId, clock.GetUtcNow());

                if (date is not null)
                {
                    plan.OccurredOn = date;
                }
                else
                {
                    plan.Report(new FinanceTurnIssue(problem == FinanceDateProblem.TimeZoneRequired
                        ? FinanceTurnIssues.TimeZoneRequired
                        : FinanceTurnIssues.DateInvalid));
                }
            }
        }

        if (Usable(AssistantFinanceFieldNames.Time, fields.Time.State))
        {
            if (fields.Time.State == AssistantFieldState.Clear)
            {
                plan.Clear.Add(FinanceDraftFields.OccurredTime);
            }
            else
            {
                plan.OccurredTime = new TimeOnly(fields.Time.Hour!.Value, fields.Time.Minute!.Value);
            }
        }

        return plan;
    }

    private async Task PlanAccountAsync(Guid userId, AssistantTextField field, bool usable, string? statedCurrency, FinanceOperationView? open,
        FinanceTurnPlan plan, CancellationToken cancellationToken)
    {
        if (usable && field.State == AssistantFieldState.Clear)
        {
            plan.Clear.Add(FinanceDraftFields.Account);
            return;
        }

        if (usable)
        {
            var resolution = FinanceMentionResolver.Account(field.Value!, await accounts.GetAllAsync(userId, cancellationToken));

            switch (resolution.Match)
            {
                case FinanceMentionMatch.Ambiguous:
                    plan.Report(new FinanceTurnIssue(FinanceTurnIssues.AccountAmbiguous, field.Value));
                    break;

                case FinanceMentionMatch.NotFound:
                    plan.Report(new FinanceTurnIssue(FinanceTurnIssues.AccountNotFound, field.Value));
                    break;

                // The transaction takes the account's currency: an account in another currency than the stated
                // one is not applied (no conversion, AI-007 FR-15).
                case FinanceMentionMatch.Resolved when statedCurrency is not null && resolution.Value!.Currency != statedCurrency:
                    plan.Report(new FinanceTurnIssue(FinanceTurnIssues.CurrencyMismatch, field.Value, resolution.Value.Name,
                        resolution.Value.Currency, statedCurrency));
                    break;

                default:
                    plan.AccountId = resolution.Value!.Id;
                    return;
            }
        }

        // A currency stated now that the draft's account does not have: the account no longer fits.
        if (plan.Currency is { } currency && open?.Account is { Currency: { } accountCurrency } current && accountCurrency != currency
            && !plan.Clear.Contains(FinanceDraftFields.Account))
        {
            plan.Clear.Add(FinanceDraftFields.Account);
            plan.Report(new FinanceTurnIssue(FinanceTurnIssues.CurrencyMismatch, current.Name, current.Name, accountCurrency, currency));
        }
    }

    private async Task PlanCategoryAsync(Guid userId, AssistantTextField field, bool usable, TransactionType? type, FinanceOperationView? open,
        FinanceTurnPlan plan, CancellationToken cancellationToken)
    {
        if (usable && field.State == AssistantFieldState.Clear)
        {
            plan.Clear.Add(FinanceDraftFields.Category);
            return;
        }

        if (usable)
        {
            var all = await categories.GetAllAsync(userId, cancellationToken);

            if (field.State == AssistantFieldState.Stated)
            {
                var (category, issue) = FinanceMentionResolver.StatedCategory(field.Value!, field.Span, type, all);

                if (category is not null)
                {
                    plan.CategoryId = category.Id;
                    plan.CategorySource = FinanceDraftCategorySource.User;
                    return;
                }

                plan.Report(new FinanceTurnIssue(issue!, field.Value));
            }
            else if (FinanceMentionResolver.Category(field.Value!, type, all, allowPartial: false) is { Match: FinanceMentionMatch.Resolved } suggestion)
            {
                plan.CategoryId = suggestion.Value!.Id;
                plan.CategorySource = FinanceDraftCategorySource.Suggested;
                return;
            }

            // A suggestion that names no single compatible category is simply not a suggestion.
        }

        // A type change the current category does not fit: the category is asked again.
        if (plan.Type is { } newType && plan.CategoryId is null && open?.Category is { Type: { } categoryType }
            && TransactionInputResolver.CategoryCompatibilityError(newType, categoryType) is not null)
        {
            plan.Clear.Add(FinanceDraftFields.Category);
        }
    }

    // ---- Context for the interpreter (no ids, no values) ----

    private static AssistantOpenDraftContext DraftContext(FinanceOperationView open)
    {
        var draft = open.Operation.Draft;
        var filled = new List<string>(8);

        if (draft.Type is not null) filled.Add(AssistantFinanceFieldNames.Type);
        if (draft.Amount is not null) filled.Add(AssistantFinanceFieldNames.Amount);
        if (draft.CurrencyMention is not null) filled.Add(AssistantFinanceFieldNames.Currency);
        if (draft.AccountId is not null) filled.Add(AssistantFinanceFieldNames.Account);
        if (draft.CategoryId is not null) filled.Add(AssistantFinanceFieldNames.Category);
        if (draft.Note is not null) filled.Add(AssistantFinanceFieldNames.Note);
        if (draft.RequestedDate is not null) filled.Add(AssistantFinanceFieldNames.Date);
        if (draft.RequestedTime is not null) filled.Add(AssistantFinanceFieldNames.Time);

        var missing = draft.MissingFields.Select(field => field switch
        {
            FinanceDraftFields.Type => AssistantFinanceFieldNames.Type,
            FinanceDraftFields.Amount => AssistantFinanceFieldNames.Amount,
            FinanceDraftFields.Account => AssistantFinanceFieldNames.Account,
            FinanceDraftFields.Category => AssistantFinanceFieldNames.Category,
            _ => AssistantFinanceFieldNames.Date
        }).ToList();

        return new AssistantOpenDraftContext(
            open.Operation.Kind,
            draft.Type switch { TransactionType.Expense => "expense", TransactionType.Income => "income", _ => null },
            filled,
            missing,
            missing.FirstOrDefault());
    }

    // A name longer than the protocol allows is left out: the service would refuse the whole request, and a
    // stated mention of it still resolves (resolution reads the user's categories, not this list).
    private static List<string> Names(IReadOnlyList<Category> all, CategoryType type) =>
        all.Where(category => category.CategoryType == type
                && category.Name.EnumerateRunes().Count() <= AssistantInterpretationProtocol.MaxCategoryNameLength)
            .Select(category => category.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCategoryNames)
            .ToList();

    private sealed class FinanceTurnPlan
    {
        public TransactionType? Type { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
        public Guid? AccountId { get; set; }
        public Guid? CategoryId { get; set; }
        public FinanceDraftCategorySource CategorySource { get; set; } = FinanceDraftCategorySource.User;
        public string? Note { get; set; }
        public DateOnly? OccurredOn { get; set; }
        public TimeOnly? OccurredTime { get; set; }
        public List<string> Clear { get; } = [];

        // The first problem found (the reply asks about it).
        public FinanceTurnIssue? Issue { get; private set; }

        public void Report(FinanceTurnIssue issue) => Issue ??= issue;

        public FinanceDraftInput ToInput() =>
            new(Type, Amount, Currency, AccountId, CategoryId, Note, OccurredOn, OccurredTime, CategorySource);

        public FinanceDraftPatch ToPatch() =>
            new(Type, Amount, Currency, AccountId, CategoryId, Note, OccurredOn, OccurredTime, Clear.Distinct().ToList(), CategorySource);
    }
}
