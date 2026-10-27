using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Application.Nutrition.Assistant;
using LifeOS.Application.Nutrition.Hydration.Assistant;
using LifeOS.Contracts.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Nutrition.Assistant;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration.Assistant;
using Microsoft.AspNetCore.Http.HttpResults;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Api.Assistant;

// AI-007 F2: assistant operations under the authenticated /api/assistant group. Transport only: the use cases
// validate, the owner comes only from the access token, and another user's conversation, operation, account or
// category is indistinguishable from a missing one. A client can create (Finance) and change a draft; only
// POST …/confirm writes to a module, and only for the draft version the user saw. There is no endpoint that
// executes anything else.
//
// AI-008: an operation's kind (read without its draft, IAssistantOperationReader) routes each request to its
// module's use cases: Finance (F2 handlers), water intakes and meals (the shared lifecycle). A field of another
// kind is a 400.
//
// Problem responses carry fixed Italian texts and a stable "code" (AssistantErrorCodes), never database or
// exception details: 400 invalid_request (field-keyed), 404 conversation_not_found / operation_not_found,
// 409 operation_already_open / version_conflict / operation_executed / operation_cancelled / operation_expired,
// 422 draft_incomplete / account_not_found / category_not_found / category_incompatible / currency_mismatch /
// transaction_rejected / future_date / record_rejected, 503 assistant_busy.
public static class AssistantOperationEndpoints
{
    public const string OperationNotFoundMessage = "Questa operazione non esiste.";

    internal static RouteGroupBuilder MapAssistantOperationEndpoints(this RouteGroupBuilder assistant)
    {
        assistant.MapGet("/conversations/{id:guid}", GetConversationAsync).WithName("GetAssistantConversation");
        assistant.MapPost("/conversations/{id:guid}/operations", CreateOperationAsync).WithName("CreateAssistantOperation");
        assistant.MapGet("/operations/{id:guid}", GetOperationAsync).WithName("GetAssistantOperation");
        assistant.MapPatch("/operations/{id:guid}", UpdateOperationAsync).WithName("UpdateAssistantOperation");
        assistant.MapPost("/operations/{id:guid}/confirm", ConfirmOperationAsync).WithName("ConfirmAssistantOperation");
        assistant.MapPost("/operations/{id:guid}/cancel", CancelOperationAsync).WithName("CancelAssistantOperation");

        return assistant;
    }

    // The conversation and its open operation of any kind (an outlived draft is marked Expired and no longer listed).
    public static async Task<Results<Ok<AssistantConversationDetailResponse>, ProblemHttpResult>> GetConversationAsync(
        Guid id,
        AuthenticatedUser user,
        GetAssistantConversationHandler conversations,
        IAssistantOperationReader reader,
        GetActiveFinanceOperationHandler finance,
        HydrationAssistantOperations hydration,
        MealAssistantOperations meals,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.HandleAsync(user.UserId, id, cancellationToken);

        if (conversation.Status != AssistantResultStatus.Ok)
        {
            return ConversationNotFound();
        }

        var active = (await reader.GetOpenAsync(user.UserId, id, cancellationToken))?.Kind switch
        {
            null => null,
            AssistantOperationKinds.HydrationIntakeCreate =>
                (await hydration.Lifecycle.GetActiveAsync(user.UserId, id, cancellationToken)).Value is { } water ? ToResponse(water) : null,
            AssistantOperationKinds.NutritionMealCreate =>
                (await meals.Lifecycle.GetActiveAsync(user.UserId, id, cancellationToken)).Value is { } meal ? ToResponse(meal) : null,
            _ => (await finance.HandleAsync(user.UserId, id, cancellationToken)).Value is { } view ? ToResponse(view) : null
        };

        return TypedResults.Ok(new AssistantConversationDetailResponse(AssistantEndpoints.ToResponse(conversation.Value!), active));
    }

    // 201 with the new Finance draft (Collecting while fields are missing, AwaitingConfirmation when complete). Water
    // and meal drafts are opened by the conversation only.
    public static async Task<Results<Created<AssistantOperationResponse>, ValidationProblem, ProblemHttpResult>> CreateOperationAsync(
        Guid id,
        CreateAssistantOperationRequest request,
        AuthenticatedUser user,
        CreateFinanceOperationHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.Kind != AssistantOperationKindNames.FinanceTransactionCreate)
        {
            return Invalid("kind", $"kind deve essere \"{AssistantOperationKindNames.FinanceTransactionCreate}\".");
        }

        if (!TryParseType(request.Type, out var type))
        {
            return Invalid(FinanceDraftFields.Type, TypeMessage);
        }

        if (!TryParseDate(request.OccurredOn, out var date))
        {
            return Invalid(FinanceDraftFields.OccurredOn, DateMessage);
        }

        if (!TryParseTime(request.OccurredTime, out var time))
        {
            return Invalid(FinanceDraftFields.OccurredTime, TimeMessage);
        }

        var result = await handler.HandleAsync(user.UserId, id,
            new FinanceDraftInput(type, request.Amount, request.Currency, request.AccountId, request.CategoryId, request.Note, date, time),
            cancellationToken);

        if (result.Status == AssistantResultStatus.Ok)
        {
            var response = ToResponse(result.Value!.View);
            return TypedResults.Created($"{AssistantEndpoints.Path}/operations/{response.Id}", response);
        }

        return result.Status switch
        {
            AssistantResultStatus.NotFound => ConversationNotFound(),
            AssistantResultStatus.Invalid => Invalid(result.Field!),
            _ => Problem(result)
        };
    }

    // Any status, Executed included (with resultId). Reading never extends the expiry.
    public static async Task<Results<Ok<AssistantOperationResponse>, ProblemHttpResult>> GetOperationAsync(
        Guid id,
        AuthenticatedUser user,
        IAssistantOperationReader reader,
        GetFinanceOperationHandler finance,
        HydrationAssistantOperations hydration,
        MealAssistantOperations meals,
        CancellationToken cancellationToken)
    {
        AssistantOperationResponse? response = (await reader.GetAsync(user.UserId, id, cancellationToken))?.Kind switch
        {
            null => null,
            AssistantOperationKinds.HydrationIntakeCreate =>
                await hydration.Lifecycle.GetAsync(user.UserId, id, cancellationToken) is { Status: AssistantResultStatus.Ok } water ? ToResponse(water.Value!) : null,
            AssistantOperationKinds.NutritionMealCreate =>
                await meals.Lifecycle.GetAsync(user.UserId, id, cancellationToken) is { Status: AssistantResultStatus.Ok } meal ? ToResponse(meal.Value!) : null,
            _ => await finance.HandleAsync(user.UserId, id, cancellationToken) is { Status: AssistantResultStatus.Ok } view ? ToResponse(view.Value!) : null
        };

        return response is null ? OperationNotFound() : TypedResults.Ok(response);
    }

    public static async Task<Results<Ok<AssistantOperationResponse>, ValidationProblem, ProblemHttpResult>> UpdateOperationAsync(
        Guid id,
        UpdateAssistantOperationRequest request,
        AuthenticatedUser user,
        IAssistantOperationReader reader,
        UpdateFinanceDraftHandler finance,
        HydrationAssistantOperations hydration,
        MealAssistantOperations meals,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedVersion is not { } expectedVersion)
        {
            return Invalid("expectedVersion");
        }

        if (!TryParseDate(request.OccurredOn, out var date))
        {
            return Invalid(FinanceDraftFields.OccurredOn, DateMessage);
        }

        if (!TryParseTime(request.OccurredTime, out var time))
        {
            return Invalid(FinanceDraftFields.OccurredTime, TimeMessage);
        }

        switch ((await reader.GetAsync(user.UserId, id, cancellationToken))?.Kind)
        {
            case null:
                return OperationNotFound();

            case AssistantOperationKinds.HydrationIntakeCreate:
            {
                if (ForeignField(request, HydrationForeignFields) is { } field)
                {
                    return NotApplicableField(field);
                }

                var result = await hydration.UpdateAsync(user.UserId, id, expectedVersion,
                    new HydrationDraftPatch(request.AmountMl, date, time, request.Clear), cancellationToken);

                return Updated(result, ToResponse);
            }

            case AssistantOperationKinds.NutritionMealCreate:
            {
                if (ForeignField(request, MealForeignFields) is { } field)
                {
                    return NotApplicableField(field);
                }

                if (!TryParseMealType(request.MealType, out var mealType))
                {
                    return Invalid(MealDraftFields.MealType);
                }

                var result = await meals.UpdateAsync(user.UserId, id, expectedVersion,
                    new MealDraftPatch(request.Description, mealType, date, time, request.Clear), cancellationToken);

                return Updated(result, ToResponse);
            }
        }

        if (ForeignField(request, FinanceForeignFields) is { } financeField)
        {
            return NotApplicableField(financeField);
        }

        if (!TryParseType(request.Type, out var type))
        {
            return Invalid(FinanceDraftFields.Type, TypeMessage);
        }

        var patch = new FinanceDraftPatch(type, request.Amount, request.Currency, request.AccountId, request.CategoryId, request.Note, date, time,
            request.Clear);

        var updated = await finance.HandleAsync(user.UserId, id, expectedVersion, patch, cancellationToken);

        return updated.Status switch
        {
            AssistantResultStatus.Ok => TypedResults.Ok(ToResponse(updated.Value!.View)),
            AssistantResultStatus.NotFound => OperationNotFound(),
            AssistantResultStatus.Invalid => Invalid(updated.Field!),
            _ => Problem(updated)
        };
    }

    // 200 with the record's id; a repeated confirmation of an executed operation returns the same id (replayed =
    // true) and never records a second one.
    public static async Task<Results<Ok<AssistantOperationConfirmationResponse>, ValidationProblem, ProblemHttpResult>> ConfirmOperationAsync(
        Guid id,
        AssistantOperationVersionRequest request,
        AuthenticatedUser user,
        IAssistantOperationReader reader,
        ConfirmFinanceOperationHandler finance,
        HydrationAssistantOperations hydration,
        MealAssistantOperations meals,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedVersion is not { } expectedVersion)
        {
            return Invalid("expectedVersion");
        }

        switch ((await reader.GetAsync(user.UserId, id, cancellationToken))?.Kind)
        {
            case null:
                return OperationNotFound();

            case AssistantOperationKinds.HydrationIntakeCreate:
                return Confirmed(await hydration.Lifecycle.ConfirmAsync(user.UserId, id, expectedVersion, cancellationToken), ToResponse);

            case AssistantOperationKinds.NutritionMealCreate:
                return Confirmed(await meals.Lifecycle.ConfirmAsync(user.UserId, id, expectedVersion, cancellationToken), ToResponse);
        }

        var result = await finance.HandleAsync(user.UserId, id, expectedVersion, cancellationToken);

        switch (result.Status)
        {
            case AssistantResultStatus.Ok:
                var operation = ToResponse(result.Value!.View);
                return TypedResults.Ok(new AssistantOperationConfirmationResponse(operation.TransactionId!.Value, !result.Value.Changed, operation,
                    operation.ResultId));

            case AssistantResultStatus.NotFound:
                return OperationNotFound();

            case AssistantResultStatus.Invalid:
                return Invalid(result.Field!);

            default:
                return Problem(result);
        }
    }

    // Idempotent for a cancelled operation; an executed or expired one cannot be cancelled (409).
    public static async Task<Results<Ok<AssistantOperationResponse>, ValidationProblem, ProblemHttpResult>> CancelOperationAsync(
        Guid id,
        AssistantOperationVersionRequest request,
        AuthenticatedUser user,
        IAssistantOperationReader reader,
        CancelFinanceOperationHandler finance,
        HydrationAssistantOperations hydration,
        MealAssistantOperations meals,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedVersion is not { } expectedVersion)
        {
            return Invalid("expectedVersion");
        }

        switch ((await reader.GetAsync(user.UserId, id, cancellationToken))?.Kind)
        {
            case null:
                return OperationNotFound();

            case AssistantOperationKinds.HydrationIntakeCreate:
                return Updated(await hydration.Lifecycle.CancelAsync(user.UserId, id, expectedVersion, cancellationToken), ToResponse);

            case AssistantOperationKinds.NutritionMealCreate:
                return Updated(await meals.Lifecycle.CancelAsync(user.UserId, id, expectedVersion, cancellationToken), ToResponse);
        }

        var result = await finance.HandleAsync(user.UserId, id, expectedVersion, cancellationToken);

        return result.Status switch
        {
            AssistantResultStatus.Ok => TypedResults.Ok(ToResponse(result.Value!.View)),
            AssistantResultStatus.NotFound => OperationNotFound(),
            AssistantResultStatus.Invalid => Invalid(result.Field!),
            _ => Problem(result)
        };
    }

    // ---- Mapping ----

    internal static AssistantOperationResponse ToResponse(FinanceOperationView view)
    {
        FinanceOperation operation = view.Operation;
        var draft = operation.Draft;

        var draftResponse = new FinanceTransactionDraftResponse(
            draft.Type?.ToString(),
            draft.Amount,
            view.Account?.Currency,
            draft.CurrencyMention,
            view.Account is { } account ? new AssistantDraftAccountResponse(account.Id, account.Name, account.Currency, account.Type?.ToString()) : null,
            view.Category is { } category
                ? new AssistantDraftCategoryResponse(category.Id, category.Name, category.Type?.ToString(), category.ParentCategoryId, category.Source.ToString())
                : null,
            draft.Note,
            draft.RequestedDate?.ToString(DateFormat, CultureInfo.InvariantCulture),
            draft.RequestedTime?.ToString(TimeFormat, CultureInfo.InvariantCulture),
            draft.Occurrence is { } occurrence
                ? new AssistantDraftOccurrenceResponse(
                    occurrence.LocalDate.ToString(DateFormat, CultureInfo.InvariantCulture),
                    occurrence.LocalTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
                    occurrence.DateSource.ToString(),
                    occurrence.TimeSource.ToString(),
                    occurrence.TimeZoneId,
                    occurrence.OccurredAtUtc,
                    view.OccursInFuture)
                : null);

        return new AssistantOperationResponse(
            operation.Id,
            operation.ConversationId,
            operation.Kind,
            operation.Status.ToString(),
            operation.DraftVersion,
            operation.PayloadVersion,
            draftResponse,
            draft.MissingFields,
            view.Issues,
            view.CanConfirm,
            operation.ExpiresAtUtc,
            operation.ResultId,
            operation.CreatedAtUtc,
            operation.UpdatedAtUtc,
            operation.ExecutedAtUtc,
            ResultId: operation.ResultId);
    }

    internal static AssistantOperationResponse ToResponse(AssistantOperationView<HydrationIntakeDraft> view)
    {
        var draft = view.Operation.Draft;

        return Generic(view, hydration: new HydrationIntakeDraftResponse(draft.AmountMl, Date(draft.RequestedDate), Time(draft.RequestedTime),
            Occurrence(draft.Occurrence, view.OccursInFuture)));
    }

    internal static AssistantOperationResponse ToResponse(AssistantOperationView<MealDraft> view)
    {
        var draft = view.Operation.Draft;

        return Generic(view, meal: new MealDraftResponse(draft.Description, draft.MealType?.ToString(), Date(draft.RequestedDate),
            Time(draft.RequestedTime), Occurrence(draft.Occurrence, view.OccursInFuture)));
    }

    private static AssistantOperationResponse Generic<TDraft>(AssistantOperationView<TDraft> view, HydrationIntakeDraftResponse? hydration = null,
        MealDraftResponse? meal = null) where TDraft : class, IAssistantDraft
    {
        var operation = view.Operation;

        return new AssistantOperationResponse(
            operation.Id,
            operation.ConversationId,
            operation.Kind,
            operation.Status.ToString(),
            operation.DraftVersion,
            operation.PayloadVersion,
            Draft: null,
            view.MissingFields,
            view.Issues,
            view.CanConfirm,
            operation.ExpiresAtUtc,
            TransactionId: null,
            operation.CreatedAtUtc,
            operation.UpdatedAtUtc,
            operation.ExecutedAtUtc,
            hydration,
            meal,
            operation.ResultId);
    }

    private static string? Date(DateOnly? date) => date?.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static string? Time(TimeOnly? time) => time?.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static AssistantDraftOccurrenceResponse? Occurrence(AssistantDiaryOccurrence? occurrence, bool isFuture) => occurrence is null
        ? null
        : new AssistantDraftOccurrenceResponse(Date(occurrence.LocalDate)!, Time(occurrence.LocalTime)!, occurrence.DateSource.ToString(),
            occurrence.TimeSource.ToString(), occurrence.TimeZoneId, occurrence.OccurredAtUtc, isFuture);

    private static Results<Ok<AssistantOperationResponse>, ValidationProblem, ProblemHttpResult> Updated<TDraft>(
        AssistantResult<AssistantOperationOutcome<TDraft>> result, Func<AssistantOperationView<TDraft>, AssistantOperationResponse> map)
        where TDraft : class, IAssistantDraft => result.Status switch
    {
        AssistantResultStatus.Ok => TypedResults.Ok(map(result.Value!.View)),
        AssistantResultStatus.NotFound => OperationNotFound(),
        AssistantResultStatus.Invalid => result.Field == "clear" ? Invalid("clear", DiaryClearMessage) : Invalid(result.Field!),
        _ => Problem(result.Status, result.Code, result.Field, result.Value is { } value ? map(value.View) : null)
    };

    private static Results<Ok<AssistantOperationConfirmationResponse>, ValidationProblem, ProblemHttpResult> Confirmed<TDraft>(
        AssistantResult<AssistantOperationOutcome<TDraft>> result, Func<AssistantOperationView<TDraft>, AssistantOperationResponse> map)
        where TDraft : class, IAssistantDraft
    {
        switch (result.Status)
        {
            case AssistantResultStatus.Ok:
                var operation = map(result.Value!.View);
                return TypedResults.Ok(new AssistantOperationConfirmationResponse(null, !result.Value.Changed, operation, operation.ResultId));

            case AssistantResultStatus.NotFound:
                return OperationNotFound();

            case AssistantResultStatus.Invalid:
                return Invalid(result.Field!);

            default:
                return Problem(result.Status, result.Code, result.Field, result.Value is { } value ? map(value.View) : null);
        }
    }

    // ---- Parsing ----

    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH:mm:ss";
    private const string TypeMessage = "Il tipo deve essere \"Expense\" o \"Income\": l'assistente non registra trasferimenti.";
    private const string DateMessage = "La data deve essere nel formato yyyy-MM-dd.";
    private const string TimeMessage = "L'ora deve essere nel formato HH:mm o HH:mm:ss.";

    private const string DiaryClearMessage =
        "clear può contenere solo i campi della bozza (amountMl, description, mealType, occurredOn, occurredTime secondo il tipo), "
        + "e non un campo impostato nella stessa richiesta.";

    // The request fields each kind does not have (the shared ones are occurredOn, occurredTime and clear).
    private static readonly string[] FinanceForeignFields = [HydrationDraftFields.AmountMl, MealDraftFields.Description, MealDraftFields.MealType];

    private static readonly string[] HydrationForeignFields =
    [
        FinanceDraftFields.Type, FinanceDraftFields.Amount, FinanceDraftFields.Currency, FinanceDraftFields.Account, FinanceDraftFields.Category,
        FinanceDraftFields.Note, MealDraftFields.Description, MealDraftFields.MealType
    ];

    private static readonly string[] MealForeignFields =
    [
        FinanceDraftFields.Type, FinanceDraftFields.Amount, FinanceDraftFields.Currency, FinanceDraftFields.Account, FinanceDraftFields.Category,
        FinanceDraftFields.Note, HydrationDraftFields.AmountMl
    ];

    // Names only, Expense or Income (Transfer is not supported by the assistant).
    private static bool TryParseType(string? value, out TransactionType? type)
    {
        type = null;

        if (value is null)
        {
            return true;
        }

        foreach (var candidate in new[] { TransactionType.Expense, TransactionType.Income })
        {
            if (string.Equals(candidate.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseMealType(string? value, out MealType? mealType)
    {
        mealType = null;

        if (value is null)
        {
            return true;
        }

        if (Enum.TryParse<MealType>(value.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _))
        {
            mealType = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseDate(string? value, out DateOnly? date)
    {
        date = null;

        if (value is null)
        {
            return true;
        }

        if (!DateOnly.TryParseExact(value.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }

    private static bool TryParseTime(string? value, out TimeOnly? time)
    {
        time = null;

        if (value is null)
        {
            return true;
        }

        if (!TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        time = parsed;
        return true;
    }

    // The first field set in the request that belongs to another kind of operation.
    private static string? ForeignField(UpdateAssistantOperationRequest request, string[] foreign)
    {
        (string Field, bool Set)[] fields =
        [
            (FinanceDraftFields.Type, request.Type is not null), (FinanceDraftFields.Amount, request.Amount is not null),
            (FinanceDraftFields.Currency, request.Currency is not null), (FinanceDraftFields.Account, request.AccountId is not null),
            (FinanceDraftFields.Category, request.CategoryId is not null), (FinanceDraftFields.Note, request.Note is not null),
            (HydrationDraftFields.AmountMl, request.AmountMl is not null), (MealDraftFields.Description, request.Description is not null),
            (MealDraftFields.MealType, request.MealType is not null)
        ];

        return fields.FirstOrDefault(field => field.Set && foreign.Contains(field.Field)).Field;
    }

    // ---- Results ----

    private static ValidationProblem NotApplicableField(string field) =>
        Invalid(field, "Questo campo non appartiene a questo tipo di operazione.");

    // Field-keyed 400 with a fixed Italian message per field.
    private static ValidationProblem Invalid(string field) => Invalid(field, field switch
    {
        "expectedVersion" => "expectedVersion è obbligatorio e deve essere una versione della bozza (da 1).",
        FinanceDraftFields.Type => TypeMessage,
        FinanceDraftFields.Amount => $"L'importo deve essere maggiore di 0, al massimo {Transaction.MaxAmount.ToString(CultureInfo.InvariantCulture)} e con al massimo {Transaction.MaxDecimalPlaces} decimali.",
        FinanceDraftFields.Currency => "La valuta deve essere un codice di 3 lettere (es. EUR).",
        FinanceDraftFields.Account => "L'identificativo del conto non è valido.",
        FinanceDraftFields.Category => "L'identificativo della categoria non è valido.",
        FinanceDraftFields.Note => $"La nota può avere al massimo {FinanceTransactionDraft.MaxNoteLength} caratteri e non può contenere il carattere NUL.",
        FinanceDraftFields.OccurredOn => $"La data deve essere compresa tra {FinanceTransactionDraft.MinOccurredOn:yyyy-MM-dd} e {FinanceTransactionDraft.MaxOccurredOn:yyyy-MM-dd}.",
        FinanceDraftFields.OccurredTime => "L'ora deve essere in secondi interi.",
        HydrationDraftFields.AmountMl => $"La quantità deve essere tra {HydrationIntake.MinAmountMl} e {HydrationIntake.MaxAmountMl} ml.",
        MealDraftFields.Description => $"La descrizione può avere al massimo {MealDraft.MaxDescriptionLength} caratteri e non può contenere il carattere NUL.",
        MealDraftFields.MealType => "Il tipo di pasto deve essere \"Breakfast\", \"Lunch\", \"Dinner\", \"Snack\" o \"Other\".",
        AssistantDiaryDraftFields.OccurredAt => "La data e l'ora non sono valide.",
        "clear" => "clear può contenere solo " + string.Join(", ", FinanceDraftPatch.ClearableFields)
            + ", e non un campo impostato nella stessa richiesta.",
        _ => "Valore non valido."
    });

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] },
            title: "Richiesta non valida.", extensions: Code(AssistantErrorCodes.InvalidRequest));

    // Conflict (409), Rejected (422) and Busy (503) of a Finance operation.
    private static ProblemHttpResult Problem(AssistantResult<FinanceOperationOutcome> result) =>
        Problem(result.Status, result.Code, result.Field, result.Value is { } value ? ToResponse(value.View) : null);

    // Conflict (409), Rejected (422) and Busy (503), with the operation as it is stored now when known.
    private static ProblemHttpResult Problem(AssistantResultStatus status, string? code, string? field, AssistantOperationResponse? operation) =>
        (status, code) switch
        {
            (AssistantResultStatus.Conflict, AssistantErrors.OperationAlreadyOpen) => Conflict(
                "Operazione già aperta.", "Questa conversazione ha già un'operazione aperta: completala o annullala prima di iniziarne un'altra.",
                AssistantErrorCodes.OperationAlreadyOpen, ("operationId", operation?.Id)),
            (AssistantResultStatus.Conflict, AssistantErrors.VersionConflict) => Conflict(
                "Bozza cambiata.", "La bozza è cambiata nel frattempo: ricaricala e controllala prima di riprovare.",
                AssistantErrorCodes.VersionConflict, ("currentVersion", operation?.DraftVersion)),
            (AssistantResultStatus.Conflict, AssistantErrors.OperationExecuted) => Conflict(
                "Operazione già registrata.", "Questa operazione è già stata registrata e non può più cambiare.",
                AssistantErrorCodes.OperationExecuted, operation?.TransactionId is { } transactionId
                    ? ("transactionId", transactionId)
                    : ("resultId", operation?.ResultId)),
            (AssistantResultStatus.Conflict, AssistantErrors.OperationCancelled) => Conflict(
                "Operazione annullata.", "Questa operazione è stata annullata e non può più cambiare.", AssistantErrorCodes.OperationCancelled),
            (AssistantResultStatus.Conflict, AssistantErrors.OperationExpired) => Conflict(
                "Bozza scaduta.", "La bozza è scaduta (24 ore dall'ultima modifica) e non può più essere modificata o confermata.",
                AssistantErrorCodes.OperationExpired),
            (AssistantResultStatus.Rejected, var rejected) => Rejected(rejected!, field, operation),
            _ => TypedResults.Problem(title: "Conversazione occupata.", detail: AssistantEndpoints.BusyMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable, extensions: Code(AssistantErrorCodes.Busy))
        };

    // 422: the draft cannot be accepted or confirmed as it is; the user can correct it. Nothing was changed.
    private static ProblemHttpResult Rejected(string code, string? field, AssistantOperationResponse? operation)
    {
        var (publicCode, detail) = code switch
        {
            FinanceOperationErrors.AccountNotFound => (AssistantErrorCodes.AccountNotFound, "Il conto indicato non esiste. Scegline un altro."),
            FinanceOperationErrors.CategoryNotFound => (AssistantErrorCodes.CategoryNotFound, "La categoria indicata non esiste. Scegline un'altra."),
            FinanceOperationErrors.CategoryIncompatible => (AssistantErrorCodes.CategoryIncompatible,
                "La categoria non è compatibile con il tipo di movimento (spesa o entrata)."),
            FinanceOperationErrors.CurrencyMismatch => (AssistantErrorCodes.CurrencyMismatch,
                "Il conto ha una valuta diversa da quella indicata: scegli un conto in quella valuta."),
            AssistantErrors.DraftIncomplete => (AssistantErrorCodes.DraftIncomplete, "La bozza è incompleta: completa i dati mancanti prima di confermare."),
            HydrationOperationErrors.FutureDate => (AssistantErrorCodes.FutureDate, "Non si può registrare acqua per un giorno futuro. Cambia la data."),
            AssistantErrors.RecordRejected => (AssistantErrorCodes.RecordRejected, "La registrazione non può essere creata così com'è. Controlla la bozza."),
            _ => (AssistantErrorCodes.TransactionRejected, "Il movimento non può essere registrato così com'è. Controlla la bozza.")
        };

        var extensions = Code(publicCode);
        extensions["field"] = field;

        if (operation is not null && publicCode == AssistantErrorCodes.DraftIncomplete)
        {
            extensions["missingFields"] = operation.MissingFields;
        }

        return TypedResults.Problem(title: "Bozza non confermabile.", detail: detail, statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: extensions);
    }

    private static ProblemHttpResult Conflict(string title, string detail, string code, (string Name, object? Value)? extra = null)
    {
        var extensions = Code(code);

        if (extra is { } value)
        {
            extensions[value.Name] = value.Value;
        }

        return TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict, extensions: extensions);
    }

    private static ProblemHttpResult ConversationNotFound() =>
        TypedResults.Problem(title: "Conversazione non trovata.", detail: AssistantEndpoints.ConversationNotFoundMessage,
            statusCode: StatusCodes.Status404NotFound, extensions: Code(AssistantErrorCodes.ConversationNotFound));

    private static ProblemHttpResult OperationNotFound() =>
        TypedResults.Problem(title: "Operazione non trovata.", detail: OperationNotFoundMessage,
            statusCode: StatusCodes.Status404NotFound, extensions: Code(AssistantErrorCodes.OperationNotFound));

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };
}
