using LifeOS.Application.Assistant;
using LifeOS.Application.Users;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration.Assistant;

namespace LifeOS.Application.Nutrition.Hydration.Assistant;

// AI-008: the Hydration module of the assistant. It turns a validated interpretation into a draft input or patch
// and applies it ONLY through HydrationAssistantOperations (the same validation, expiry and versions as the HTTP
// API). It never confirms, never cancels and never writes Hydration: an intake is recorded only by POST …/confirm.
//
// What the model said is never used as it is:
//   - quantity: the stated amount and unit (ml, cl, l) converted to whole millilitres here (HydrationQuantity);
//     an amount without a unit, a unit without an amount or a container ("un bicchiere") is asked, never guessed;
//   - date: a date expression resolved on the backend in the user's time zone; the time of day follows the diary
//     conventions (today → now, another day → 12:00);
//   - fields the model marked ambiguous are asked, not applied.
// The context it adds to the interpreter is nothing: no intake history, no target, no settings.
public sealed class HydrationAssistantModule(HydrationAssistantOperations operations, IUserRepository users, TimeProvider clock) : IAssistantModule
{
    public const string UnitMissing = "unit_missing";
    public const string QuantityMissing = "quantity_missing";
    public const string AmountInvalid = "amount_invalid";

    public string Kind => AssistantOperationKinds.HydrationIntakeCreate;

    public string Label => "l'acqua che bevi";

    public string DraftName => "acqua";

    public bool Opens(AssistantIntent intent) => intent == AssistantIntent.HydrationIntake;

    public async Task<AssistantOpenDraft?> GetOpenDraftAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        (await operations.GetActiveAsync(userId, conversationId, cancellationToken)).Value is { } open ? OpenDraft(open) : null;

    public Task ContributeAsync(Guid userId, AssistantOpenDraft? open, AssistantContextSections context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<AssistantCapabilityOutcome> ApplyAsync(Guid userId, Guid conversationId, AssistantOpenDraft? interpreted,
        AssistantInterpretation interpretation, CancellationToken cancellationToken)
    {
        var current = (await operations.GetActiveAsync(userId, conversationId, cancellationToken)).Value;
        var open = interpreted?.Context.Kind == Kind ? current : null;

        if (open is not null && OpenDraft(open).Snapshot != interpreted!.Snapshot)
        {
            return AssistantCapabilityOutcome.Failed(AssistantErrors.DraftChanged);
        }

        switch (interpretation.Intent)
        {
            case AssistantIntent.Clarification:
                return AssistantCapabilityOutcome.Answered(open is null ? AssistantCoreReplies.NotUnderstood : HydrationAssistantReplies.NotUnderstoodWith(open));

            // Never a confirmation: only POST …/confirm records an intake.
            case AssistantIntent.DraftConfirm:
                return AssistantCapabilityOutcome.Answered(open is null ? AssistantCoreReplies.NothingToConfirm : HydrationAssistantReplies.ConfirmRequested(open));

            case AssistantIntent.DraftCancel:
                return AssistantCapabilityOutcome.Answered(open is null ? AssistantCoreReplies.NothingToCancel : HydrationAssistantReplies.CancelWithButton);

            case AssistantIntent.HydrationIntake when open is not null:
                return AssistantCapabilityOutcome.Answered(HydrationAssistantReplies.AlreadyOpen);

            case AssistantIntent.DraftUpdate when open is null:
                return AssistantCapabilityOutcome.Answered(AssistantCoreReplies.NothingToUpdate);

            case not (AssistantIntent.HydrationIntake or AssistantIntent.DraftUpdate):
                return AssistantCapabilityOutcome.Failed(AssistantErrors.AssistantInvalidOutput);
        }

        var plan = await PlanAsync(userId, interpretation, cancellationToken);

        var result = open is null
            ? await operations.CreateAsync(userId, conversationId, plan.ToInput(), cancellationToken)
            : await operations.UpdateAsync(userId, open.Operation.Id, open.Operation.DraftVersion, plan.ToPatch(), cancellationToken);

        return result.Status switch
        {
            AssistantResultStatus.Ok => AssistantCapabilityOutcome.Answered(HydrationAssistantReplies.AfterChange(result.Value!.View, plan.Issue)),

            // A value the draft cannot hold got past the protocol checks.
            AssistantResultStatus.Invalid => AssistantCapabilityOutcome.Failed(AssistantErrors.AssistantInvalidOutput),

            // The draft moved: edited, confirmed, cancelled, expired, or another one was opened.
            _ => AssistantCapabilityOutcome.Failed(AssistantErrors.DraftChanged)
        };
    }

    private async Task<HydrationTurnPlan> PlanAsync(Guid userId, AssistantInterpretation interpretation, CancellationToken cancellationToken)
    {
        var fields = interpretation.Hydration;
        var plan = new HydrationTurnPlan();

        bool Usable(string name, AssistantFieldState state)
        {
            if (state == AssistantFieldState.Absent)
            {
                return false;
            }

            if (interpretation.AmbiguousFields.Contains(name))
            {
                plan.Report(new AssistantDiaryIssue(AssistantDiaryIssues.FieldAmbiguous, name));
                return false;
            }

            return true;
        }

        var amount = Usable(AssistantHydrationFieldNames.Amount, fields.Amount.State);
        var unit = Usable(AssistantHydrationFieldNames.Unit, fields.Unit.State);

        if (amount && unit)
        {
            if (HydrationQuantity.ToMillilitres(fields.Amount.Value, fields.Unit.Value) is { } millilitres)
            {
                plan.AmountMl = millilitres;
            }
            else
            {
                plan.Report(new AssistantDiaryIssue(AmountInvalid));
            }
        }
        else if (amount)
        {
            plan.Report(new AssistantDiaryIssue(UnitMissing));
        }
        else if (unit)
        {
            plan.Report(new AssistantDiaryIssue(QuantityMissing));
        }

        var dateUsable = Usable(AssistantHydrationFieldNames.Date, fields.Date.State);
        var timeUsable = Usable(AssistantHydrationFieldNames.Time, fields.Time.State);
        var timeZoneId = dateUsable ? (await users.GetByIdAsync(userId, cancellationToken))?.TimeZoneId : null;

        plan.When = AssistantDiaryWhen.Plan(fields.Date, dateUsable, fields.Time, timeUsable, timeZoneId, clock.GetUtcNow(), plan.Report);

        return plan;
    }

    // ---- Context for the interpreter (no ids, no values) ----

    private static AssistantOpenDraft OpenDraft(AssistantOperationView<HydrationIntakeDraft> open)
    {
        var draft = open.Operation.Draft;
        var filled = new List<string>(4);

        if (draft.AmountMl is not null)
        {
            filled.Add(AssistantHydrationFieldNames.Amount);
            filled.Add(AssistantHydrationFieldNames.Unit);
        }

        if (draft.RequestedDate is not null) filled.Add(AssistantHydrationFieldNames.Date);
        if (draft.RequestedTime is not null) filled.Add(AssistantHydrationFieldNames.Time);

        var missing = open.MissingFields
            .Select(field => field == HydrationDraftFields.AmountMl ? AssistantHydrationFieldNames.Amount : AssistantHydrationFieldNames.Date)
            .ToList();

        return new AssistantOpenDraft(new AssistantOperationSnapshot(open.Operation.Id, open.Operation.DraftVersion),
            new AssistantOpenDraftContext(open.Operation.Kind, null, filled, missing, missing.FirstOrDefault()));
    }

    private sealed class HydrationTurnPlan
    {
        public int? AmountMl { get; set; }

        public AssistantDiaryWhen When { get; set; } = null!;

        // The first problem found (the reply asks about it).
        public AssistantDiaryIssue? Issue { get; private set; }

        public void Report(AssistantDiaryIssue issue) => Issue ??= issue;

        public HydrationDraftInput ToInput() => new(AmountMl, When.OccurredOn, When.OccurredTime);

        public HydrationDraftPatch ToPatch()
        {
            var clear = new List<string>(2);

            if (When.ClearDate) clear.Add(HydrationDraftFields.OccurredOn);
            if (When.ClearTime) clear.Add(HydrationDraftFields.OccurredTime);

            return new HydrationDraftPatch(AmountMl, When.OccurredOn, When.OccurredTime, clear);
        }
    }
}

// AI-008 replies for water, Italian, deterministic templates built from the stored draft (never the model's text).
public static class HydrationAssistantReplies
{
    public const string ConfirmWithButton = "L'acqua è pronta. Confermala con il pulsante.";
    public const string CancelWithButton = "Per annullare la bozza usa il pulsante Annulla.";
    public const string AlreadyOpen = "C'è già dell'acqua in preparazione. Confermala o annullala prima di iniziarne un'altra.";
    public const string AmountQuestion = "Quanta acqua hai bevuto? Indicala in ml, cl o litri.";

    public static string AfterChange(AssistantOperationView<HydrationIntakeDraft> view, AssistantDiaryIssue? issue) =>
        issue is not null ? Issue(issue) : Next(view);

    public static string ConfirmRequested(AssistantOperationView<HydrationIntakeDraft> open) =>
        open.CanConfirm ? ConfirmWithButton : "Non è ancora pronta. " + Next(open);

    public static string NotUnderstoodWith(AssistantOperationView<HydrationIntakeDraft> open) => "Non ho capito. " + Next(open);

    // The next thing the draft needs, or that it is ready.
    public static string Next(AssistantOperationView<HydrationIntakeDraft> view)
    {
        var draft = view.Operation.Draft;

        if (view.Issues.Contains(HydrationOperationErrors.FutureDate))
        {
            return "Non posso registrare acqua per un giorno futuro. Quando l'hai bevuta?";
        }

        if (view.MissingFields.Count > 0)
        {
            return view.MissingFields[0] == HydrationDraftFields.AmountMl
                ? AmountQuestion
                : "Imposta il fuso orario nelle impostazioni per completare la data.";
        }

        var ready = $"Ho preparato {HydrationQuantity.Text(draft.AmountMl!.Value)} d'acqua. Vuoi modificarla?";

        return view.OccursInFuture ? ready + " L'ora è nel futuro: controllala prima di confermare." : ready;
    }

    private static string Issue(AssistantDiaryIssue issue) => issue.Code switch
    {
        HydrationAssistantModule.AmountInvalid =>
            $"La quantità deve essere tra {HydrationIntake.MinAmountMl} e {HydrationIntake.MaxAmountMl} ml, in millilitri interi. " + AmountQuestion,
        HydrationAssistantModule.UnitMissing => "In che unità? Indica ml, cl o litri.",
        HydrationAssistantModule.QuantityMissing => AmountQuestion,
        AssistantDiaryIssues.DateInvalid => "La data non è valida. Quando l'hai bevuta?",
        AssistantDiaryIssues.TimeZoneRequired => "Imposta il fuso orario nelle impostazioni per usare date come «ieri».",
        _ => issue.Field switch
        {
            AssistantHydrationFieldNames.Amount or AssistantHydrationFieldNames.Unit => "Non ho capito la quantità. " + AmountQuestion,
            AssistantHydrationFieldNames.Date or AssistantHydrationFieldNames.Time => "Non ho capito quando. Quando l'hai bevuta?",
            _ => "Non ho capito un dettaglio. Puoi ripeterlo?"
        }
    };
}
