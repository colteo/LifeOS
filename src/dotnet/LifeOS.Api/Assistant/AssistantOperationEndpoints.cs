using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Assistant;
using LifeOS.Contracts.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Transactions;
using Microsoft.AspNetCore.Http.HttpResults;
using FinanceOperation = LifeOS.Domain.Assistant.AssistantOperation<LifeOS.Domain.Finance.Assistant.FinanceTransactionDraft>;

namespace LifeOS.Api.Assistant;

// AI-007 F2: assistant operations (v1: one Expense or Income) under the authenticated /api/assistant group.
// Transport only: the use cases validate, the owner comes only from the access token, and another user's
// conversation, operation, account or category is indistinguishable from a missing one. A client can create
// and change a draft; only POST …/confirm records a transaction, and only for the draft version the user saw.
// There is no endpoint that executes anything else.
//
// Problem responses carry fixed Italian texts and a stable "code" (AssistantErrorCodes), never database or
// exception details: 400 invalid_request (field-keyed), 404 conversation_not_found / operation_not_found,
// 409 operation_already_open / version_conflict / operation_executed / operation_cancelled / operation_expired,
// 422 draft_incomplete / account_not_found / category_not_found / category_incompatible / currency_mismatch /
// transaction_rejected, 503 assistant_busy.
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

    // The conversation and its open operation (an outlived draft is marked Expired and no longer listed).
    public static async Task<Results<Ok<AssistantConversationDetailResponse>, ProblemHttpResult>> GetConversationAsync(
        Guid id,
        AuthenticatedUser user,
        GetAssistantConversationHandler conversations,
        GetActiveFinanceOperationHandler operations,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.HandleAsync(user.UserId, id, cancellationToken);

        if (conversation.Status != AssistantResultStatus.Ok)
        {
            return ConversationNotFound();
        }

        var active = await operations.HandleAsync(user.UserId, id, cancellationToken);

        return TypedResults.Ok(new AssistantConversationDetailResponse(
            AssistantEndpoints.ToResponse(conversation.Value!), active.Value is { } view ? ToResponse(view) : null));
    }

    // 201 with the new draft (Collecting while fields are missing, AwaitingConfirmation when complete).
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

    // Any status, Executed included (with transactionId). Reading never extends the expiry.
    public static async Task<Results<Ok<AssistantOperationResponse>, ProblemHttpResult>> GetOperationAsync(
        Guid id,
        AuthenticatedUser user,
        GetFinanceOperationHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, id, cancellationToken);

        return result.Status == AssistantResultStatus.Ok ? TypedResults.Ok(ToResponse(result.Value!)) : OperationNotFound();
    }

    public static async Task<Results<Ok<AssistantOperationResponse>, ValidationProblem, ProblemHttpResult>> UpdateOperationAsync(
        Guid id,
        UpdateAssistantOperationRequest request,
        AuthenticatedUser user,
        UpdateFinanceDraftHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedVersion is not { } expectedVersion)
        {
            return Invalid("expectedVersion");
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

        var patch = new FinanceDraftPatch(type, request.Amount, request.Currency, request.AccountId, request.CategoryId, request.Note, date, time,
            request.Clear);

        var result = await handler.HandleAsync(user.UserId, id, expectedVersion, patch, cancellationToken);

        return result.Status switch
        {
            AssistantResultStatus.Ok => TypedResults.Ok(ToResponse(result.Value!.View)),
            AssistantResultStatus.NotFound => OperationNotFound(),
            AssistantResultStatus.Invalid => Invalid(result.Field!),
            _ => Problem(result)
        };
    }

    // 200 with the transaction id; a repeated confirmation of an executed operation returns the same id
    // (replayed = true) and never records a second transaction.
    public static async Task<Results<Ok<AssistantOperationConfirmationResponse>, ValidationProblem, ProblemHttpResult>> ConfirmOperationAsync(
        Guid id,
        AssistantOperationVersionRequest request,
        AuthenticatedUser user,
        ConfirmFinanceOperationHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedVersion is not { } expectedVersion)
        {
            return Invalid("expectedVersion");
        }

        var result = await handler.HandleAsync(user.UserId, id, expectedVersion, cancellationToken);

        switch (result.Status)
        {
            case AssistantResultStatus.Ok:
                var operation = ToResponse(result.Value!.View);
                return TypedResults.Ok(new AssistantOperationConfirmationResponse(operation.TransactionId!.Value, !result.Value.Changed, operation));

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
        CancelFinanceOperationHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedVersion is not { } expectedVersion)
        {
            return Invalid("expectedVersion");
        }

        var result = await handler.HandleAsync(user.UserId, id, expectedVersion, cancellationToken);

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
            operation.ResultTransactionId,
            operation.CreatedAtUtc,
            operation.UpdatedAtUtc,
            operation.ExecutedAtUtc);
    }

    // ---- Parsing ----

    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH:mm:ss";
    private const string TypeMessage = "Il tipo deve essere \"Expense\" o \"Income\": l'assistente non registra trasferimenti.";
    private const string DateMessage = "La data deve essere nel formato yyyy-MM-dd.";
    private const string TimeMessage = "L'ora deve essere nel formato HH:mm o HH:mm:ss.";

    // Names only, Expense or Income (Transfer is not supported by the assistant in v1).
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

    // ---- Results ----

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
        "clear" => "clear può contenere solo " + string.Join(", ", FinanceDraftPatch.ClearableFields)
            + ", e non un campo impostato nella stessa richiesta.",
        _ => "Valore non valido."
    });

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] },
            title: "Richiesta non valida.", extensions: Code(AssistantErrorCodes.InvalidRequest));

    // Conflict (409), Rejected (422) and Busy (503).
    private static ProblemHttpResult Problem(AssistantResult<FinanceOperationOutcome> result)
    {
        var operation = result.Value?.View.Operation;

        return (result.Status, result.Code) switch
        {
            (AssistantResultStatus.Conflict, AssistantErrors.OperationAlreadyOpen) => Conflict(
                "Operazione già aperta.", "Questa conversazione ha già un'operazione aperta: completala o annullala prima di iniziarne un'altra.",
                AssistantErrorCodes.OperationAlreadyOpen, ("operationId", operation?.Id)),
            (AssistantResultStatus.Conflict, AssistantErrors.VersionConflict) => Conflict(
                "Bozza cambiata.", "La bozza è cambiata nel frattempo: ricaricala e controllala prima di riprovare.",
                AssistantErrorCodes.VersionConflict, ("currentVersion", operation?.DraftVersion)),
            (AssistantResultStatus.Conflict, AssistantErrors.OperationExecuted) => Conflict(
                "Operazione già registrata.", "Questa operazione è già stata registrata e non può più cambiare.",
                AssistantErrorCodes.OperationExecuted, ("transactionId", operation?.ResultTransactionId)),
            (AssistantResultStatus.Conflict, AssistantErrors.OperationCancelled) => Conflict(
                "Operazione annullata.", "Questa operazione è stata annullata e non può più cambiare.", AssistantErrorCodes.OperationCancelled),
            (AssistantResultStatus.Conflict, AssistantErrors.OperationExpired) => Conflict(
                "Bozza scaduta.", "La bozza è scaduta (24 ore dall'ultima modifica) e non può più essere modificata o confermata.",
                AssistantErrorCodes.OperationExpired),
            (AssistantResultStatus.Rejected, var code) => Rejected(code!, result.Field, result.Value?.View),
            _ => TypedResults.Problem(title: "Conversazione occupata.", detail: AssistantEndpoints.BusyMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable, extensions: Code(AssistantErrorCodes.Busy))
        };
    }

    // 422: the draft cannot be accepted or confirmed as it is; the user can correct it. Nothing was changed.
    private static ProblemHttpResult Rejected(string code, string? field, FinanceOperationView? view)
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
            _ => (AssistantErrorCodes.TransactionRejected, "Il movimento non può essere registrato così com'è. Controlla la bozza.")
        };

        var extensions = Code(publicCode);
        extensions["field"] = field;

        if (view is not null && publicCode == AssistantErrorCodes.DraftIncomplete)
        {
            extensions["missingFields"] = view.Operation.Draft.MissingFields;
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
