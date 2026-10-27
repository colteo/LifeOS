using LifeOS.Application.Assistant;
using LifeOS.Application.Users;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Nutrition.Assistant;

namespace LifeOS.Application.Nutrition.Assistant;

// AI-008: the meal journal module of the assistant. It turns a validated interpretation into a draft input or patch
// and applies it ONLY through MealAssistantOperations. It never confirms, never cancels, never writes the journal and
// never asks for a nutrition estimate: a meal is recorded only by POST …/confirm, as plain text.
//
// What the model said is never used as it is:
//   - description: the user's own words (the validator refuses anything not found verbatim in the message), so no
//     ingredient, quantity or nutrition value can be added; a description is enough to prepare the meal — no
//     further question when the user named what they ate;
//   - meal type: only when stated (colazione, pranzo, cena, spuntino), otherwise left empty, never guessed from the
//     time;
//   - date: a date expression resolved on the backend in the user's time zone (today → now, another day → 12:00);
//   - fields the model marked ambiguous are asked, not applied.
// The context it adds to the interpreter is nothing: no meal history, no targets, no nutrition.
public sealed class MealAssistantModule(MealAssistantOperations operations, IUserRepository users, TimeProvider clock) : IAssistantModule
{
    public string Kind => AssistantOperationKinds.NutritionMealCreate;

    public string Label => "i pasti";

    public string DraftName => "pasto";

    public bool Opens(AssistantIntent intent) => intent == AssistantIntent.NutritionMeal;

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
                return AssistantCapabilityOutcome.Answered(open is null ? AssistantCoreReplies.NotUnderstood : MealAssistantReplies.NotUnderstoodWith(open));

            // Never a confirmation: only POST …/confirm records a meal.
            case AssistantIntent.DraftConfirm:
                return AssistantCapabilityOutcome.Answered(open is null ? AssistantCoreReplies.NothingToConfirm : MealAssistantReplies.ConfirmRequested(open));

            case AssistantIntent.DraftCancel:
                return AssistantCapabilityOutcome.Answered(open is null ? AssistantCoreReplies.NothingToCancel : MealAssistantReplies.CancelWithButton);

            case AssistantIntent.NutritionMeal when open is not null:
                return AssistantCapabilityOutcome.Answered(MealAssistantReplies.AlreadyOpen);

            case AssistantIntent.DraftUpdate when open is null:
                return AssistantCapabilityOutcome.Answered(AssistantCoreReplies.NothingToUpdate);

            case not (AssistantIntent.NutritionMeal or AssistantIntent.DraftUpdate):
                return AssistantCapabilityOutcome.Failed(AssistantErrors.AssistantInvalidOutput);
        }

        var plan = await PlanAsync(userId, interpretation, cancellationToken);

        var result = open is null
            ? await operations.CreateAsync(userId, conversationId, plan.ToInput(), cancellationToken)
            : await operations.UpdateAsync(userId, open.Operation.Id, open.Operation.DraftVersion, plan.ToPatch(), cancellationToken);

        return result.Status switch
        {
            AssistantResultStatus.Ok => AssistantCapabilityOutcome.Answered(MealAssistantReplies.AfterChange(result.Value!.View, plan.Issue)),
            AssistantResultStatus.Invalid => AssistantCapabilityOutcome.Failed(AssistantErrors.AssistantInvalidOutput),
            _ => AssistantCapabilityOutcome.Failed(AssistantErrors.DraftChanged)
        };
    }

    public static MealType? ParseMealType(string? value) => value switch
    {
        "breakfast" => MealType.Breakfast,
        "lunch" => MealType.Lunch,
        "dinner" => MealType.Dinner,
        "snack" => MealType.Snack,
        "other" => MealType.Other,
        _ => null
    };

    private async Task<MealTurnPlan> PlanAsync(Guid userId, AssistantInterpretation interpretation, CancellationToken cancellationToken)
    {
        var fields = interpretation.Meal;
        var plan = new MealTurnPlan();

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

        if (Usable(AssistantMealFieldNames.Description, fields.Description.State))
        {
            plan.Description = fields.Description.Value;
        }

        if (Usable(AssistantMealFieldNames.MealType, fields.MealType.State))
        {
            if (fields.MealType.State == AssistantFieldState.Clear)
            {
                plan.ClearMealType = true;
            }
            else
            {
                plan.MealType = ParseMealType(fields.MealType.Value);
            }
        }

        var dateUsable = Usable(AssistantMealFieldNames.Date, fields.Date.State);
        var timeUsable = Usable(AssistantMealFieldNames.Time, fields.Time.State);
        var timeZoneId = dateUsable ? (await users.GetByIdAsync(userId, cancellationToken))?.TimeZoneId : null;

        plan.When = AssistantDiaryWhen.Plan(fields.Date, dateUsable, fields.Time, timeUsable, timeZoneId, clock.GetUtcNow(), plan.Report);

        return plan;
    }

    // ---- Context for the interpreter (no ids, no values) ----

    private static AssistantOpenDraft OpenDraft(AssistantOperationView<MealDraft> open)
    {
        var draft = open.Operation.Draft;
        var filled = new List<string>(4);

        if (draft.Description is not null) filled.Add(AssistantMealFieldNames.Description);
        if (draft.MealType is not null) filled.Add(AssistantMealFieldNames.MealType);
        if (draft.RequestedDate is not null) filled.Add(AssistantMealFieldNames.Date);
        if (draft.RequestedTime is not null) filled.Add(AssistantMealFieldNames.Time);

        var missing = open.MissingFields
            .Select(field => field == MealDraftFields.Description ? AssistantMealFieldNames.Description : AssistantMealFieldNames.Date)
            .ToList();

        return new AssistantOpenDraft(new AssistantOperationSnapshot(open.Operation.Id, open.Operation.DraftVersion),
            new AssistantOpenDraftContext(open.Operation.Kind, null, filled, missing, missing.FirstOrDefault()));
    }

    private sealed class MealTurnPlan
    {
        public string? Description { get; set; }

        public MealType? MealType { get; set; }

        public bool ClearMealType { get; set; }

        public AssistantDiaryWhen When { get; set; } = null!;

        // The first problem found (the reply asks about it).
        public AssistantDiaryIssue? Issue { get; private set; }

        public void Report(AssistantDiaryIssue issue) => Issue ??= issue;

        public MealDraftInput ToInput() => new(Description, MealType, When.OccurredOn, When.OccurredTime);

        public MealDraftPatch ToPatch()
        {
            var clear = new List<string>(3);

            if (ClearMealType) clear.Add(MealDraftFields.MealType);
            if (When.ClearDate) clear.Add(MealDraftFields.OccurredOn);
            if (When.ClearTime) clear.Add(MealDraftFields.OccurredTime);

            return new MealDraftPatch(Description, MealType, When.OccurredOn, When.OccurredTime, clear);
        }
    }
}

// AI-008 replies for meals, Italian, deterministic templates built from the stored draft (never the model's text).
public static class MealAssistantReplies
{
    public const string ConfirmWithButton = "Il pasto è pronto. Confermalo con il pulsante.";
    public const string CancelWithButton = "Per annullare la bozza usa il pulsante Annulla.";
    public const string AlreadyOpen = "C'è già un pasto in preparazione. Confermalo o annullalo prima di iniziarne un altro.";
    public const string DescriptionQuestion = "Cosa hai mangiato?";

    public static string AfterChange(AssistantOperationView<MealDraft> view, AssistantDiaryIssue? issue) =>
        issue is not null ? Issue(issue) : Next(view);

    public static string ConfirmRequested(AssistantOperationView<MealDraft> open) =>
        open.CanConfirm ? ConfirmWithButton : "Non è ancora pronto. " + Next(open);

    public static string NotUnderstoodWith(AssistantOperationView<MealDraft> open) => "Non ho capito. " + Next(open);

    // The next thing the draft needs, or that it is ready.
    public static string Next(AssistantOperationView<MealDraft> view)
    {
        if (view.MissingFields.Count > 0)
        {
            return view.MissingFields[0] == MealDraftFields.Description
                ? DescriptionQuestion
                : "Imposta il fuso orario nelle impostazioni per completare la data.";
        }

        const string ready = "Ho preparato il pasto. Vuoi modificarlo?";

        return view.OccursInFuture ? ready + " L'ora è nel futuro: controllala prima di confermare." : ready;
    }

    private static string Issue(AssistantDiaryIssue issue) => issue.Code switch
    {
        AssistantDiaryIssues.DateInvalid => "La data non è valida. Quando hai mangiato?",
        AssistantDiaryIssues.TimeZoneRequired => "Imposta il fuso orario nelle impostazioni per usare date come «ieri».",
        _ => issue.Field switch
        {
            AssistantMealFieldNames.Description => "Non ho capito cosa hai mangiato. " + DescriptionQuestion,
            AssistantMealFieldNames.MealType => "Non ho capito di quale pasto si tratta. È colazione, pranzo, cena o uno spuntino?",
            AssistantMealFieldNames.Date or AssistantMealFieldNames.Time => "Non ho capito quando. Quando hai mangiato?",
            _ => "Non ho capito un dettaglio. Puoi ripeterlo?"
        }
    };
}
