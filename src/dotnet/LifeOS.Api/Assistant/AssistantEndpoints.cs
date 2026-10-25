using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Assistant;
using LifeOS.Contracts.Assistant;
using LifeOS.Domain.Assistant;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Assistant;

// AI-007 F1: LifeOS Assistant conversations and messages. Transport only. The owner comes only from the
// access token; another user's conversation or message is a 404, exactly like a missing one. Problem
// responses carry fixed Italian texts and a stable "code" (AssistantErrorCodes), never database or
// exception details. Validation failures are field-keyed 400s.
//
// Messages go through AssistantTurnProcessor only: in F1 a sent message is stored and its turn fails at once
// with assistant_unavailable (no AI is integrated). Completing a turn is not reachable over HTTP.
public static class AssistantEndpoints
{
    public const string Path = "/api/assistant";

    public const string ConversationNotFoundMessage = "Questa conversazione non esiste.";
    public const string MessageNotFoundMessage = "Questo messaggio non esiste.";
    public const string TurnInProgressMessage = "L'assistente sta ancora elaborando un messaggio di questa conversazione. Riprova tra poco.";
    public const string ClientMessageIdReusedMessage = "Questo identificativo di messaggio è già stato usato per un testo diverso.";
    public const string TurnSupersededMessage = "Si può riprovare solo l'ultimo messaggio della conversazione. Invialo di nuovo come nuovo messaggio.";
    public const string BusyMessage = "La conversazione è occupata da altre richieste. Riprova.";

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var assistant = endpoints.MapGroup(Path)
            .RequireAuthorization();

        assistant.MapPost("/conversations", CreateConversationAsync).WithName("CreateAssistantConversation");
        assistant.MapGet("/conversations", GetConversationsAsync).WithName("GetAssistantConversations");
        assistant.MapDelete("/conversations/{id:guid}", DeleteConversationAsync).WithName("DeleteAssistantConversation");
        assistant.MapGet("/conversations/{id:guid}/messages", GetMessagesAsync).WithName("GetAssistantMessages");
        assistant.MapPost("/conversations/{id:guid}/messages", SendMessageAsync).WithName("SendAssistantMessage");
        assistant.MapPost("/messages/{id:guid}/retry", RetryMessageAsync).WithName("RetryAssistantMessage");

        // AI-007 F2: GET /conversations/{id} and the operations (AssistantOperationEndpoints).
        assistant.MapAssistantOperationEndpoints();

        return endpoints;
    }

    // 201 for a new conversation, 200 for a replay of the same clientConversationId.
    public static async Task<Results<Created<AssistantConversationResponse>, Ok<AssistantConversationResponse>, ValidationProblem, ProblemHttpResult>>
        CreateConversationAsync(
            CreateAssistantConversationRequest request,
            AuthenticatedUser user,
            CreateAssistantConversationHandler handler,
            CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, request.ClientConversationId ?? Guid.Empty, cancellationToken);

        switch (result.Status)
        {
            case AssistantResultStatus.Ok:
                var response = ToResponse(result.Value!.Conversation);
                return result.Value.Created
                    ? TypedResults.Created($"{Path}/conversations/{response.Id}/messages", response)
                    : TypedResults.Ok(response);

            case AssistantResultStatus.Invalid:
                return Invalid("clientConversationId", "L'identificativo client della conversazione è obbligatorio.");

            default:
                return Problem(result);
        }
    }

    // limit: 1–50 (default 20). cursor: the previous page's NextCursor.
    public static async Task<Results<Ok<AssistantConversationPageResponse>, ValidationProblem, ProblemHttpResult>> GetConversationsAsync(
        AuthenticatedUser user,
        ListAssistantConversationsHandler handler,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        AssistantConversationCursor? before = null;

        if (cursor is not null && !TryParseCursor(cursor, out before))
        {
            return Invalid("cursor", "Il cursore non è valido. Ricomincia dalla prima pagina.");
        }

        var result = await handler.HandleAsync(user.UserId, before, limit, cancellationToken);

        if (result.Status == AssistantResultStatus.Invalid)
        {
            return Invalid("limit", $"limit deve essere compreso tra 1 e {AssistantPaging.MaxConversations}.");
        }

        var conversations = result.Value!;
        var take = limit ?? AssistantPaging.DefaultConversations;
        var next = conversations.Count == take ? FormatCursor(new AssistantConversationCursor(conversations[^1].UpdatedAtUtc, conversations[^1].Id)) : null;

        return TypedResults.Ok(new AssistantConversationPageResponse(conversations.Select(ToResponse).ToList(), next));
    }

    // Hard delete of the conversation and its messages. Like the other LifeOS deletes, a missing (or
    // another user's, or already deleted) conversation is a 404.
    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteConversationAsync(
        Guid id,
        AuthenticatedUser user,
        DeleteAssistantConversationHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, id, cancellationToken) == AssistantResultStatus.Ok
            ? TypedResults.NoContent()
            : ConversationNotFound();

    // afterSequence: ≥ 0 (default 0, from the first message). limit: 1–200 (default 50). Reading fails a
    // turn whose lease has ended (Interrupted) before the messages are returned.
    public static async Task<Results<Ok<AssistantMessagePageResponse>, ValidationProblem, ProblemHttpResult>> GetMessagesAsync(
        Guid id,
        AuthenticatedUser user,
        GetAssistantConversationMessagesHandler handler,
        CancellationToken cancellationToken,
        int? afterSequence = null,
        int? limit = null)
    {
        var result = await handler.HandleAsync(user.UserId, id, afterSequence, limit, cancellationToken);

        switch (result.Status)
        {
            case AssistantResultStatus.Ok:
                var page = result.Value!;
                var take = limit ?? AssistantPaging.DefaultMessages;
                int? next = page.Messages.Count == take ? page.Messages[^1].Sequence : null;
                return TypedResults.Ok(new AssistantMessagePageResponse(ToResponse(page.Conversation), page.Messages.Select(ToResponse).ToList(), next));

            case AssistantResultStatus.Invalid when result.Field == "afterSequence":
                return Invalid("afterSequence", "afterSequence non può essere negativo.");

            case AssistantResultStatus.Invalid:
                return Invalid("limit", $"limit deve essere compreso tra 1 e {AssistantPaging.MaxMessages}.");

            default:
                return Problem(result);
        }
    }

    // 201 when the message is stored now, 200 for a replay of the same clientMessageId (same text). Either
    // way the body is the message's persisted turn: in F1 Failed with assistant_unavailable.
    public static async Task<Results<Created<AssistantTurnResponse>, Ok<AssistantTurnResponse>, ValidationProblem, ProblemHttpResult>>
        SendMessageAsync(
            Guid id,
            SendAssistantMessageRequest request,
            AuthenticatedUser user,
            AssistantTurnProcessor processor,
            CancellationToken cancellationToken)
    {
        var result = await processor.SendAsync(user.UserId, id,
            new SendAssistantMessageCommand(request.ClientMessageId ?? Guid.Empty, request.Text ?? ""), cancellationToken);

        switch (result.Status)
        {
            case AssistantResultStatus.Ok when result.Value!.Replayed:
                return TypedResults.Ok(ToResponse(result.Value));

            case AssistantResultStatus.Ok:
                return TypedResults.Created($"{Path}/conversations/{id}/messages", ToResponse(result.Value));

            case AssistantResultStatus.Invalid when result.Field == "clientMessageId":
                return Invalid("clientMessageId", "L'identificativo client del messaggio è obbligatorio.");

            case AssistantResultStatus.Invalid:
                return Invalid("text",
                    $"Il messaggio deve avere da 1 a {AssistantMessage.MaxUserContentLength} caratteri e non può contenere il carattere NUL.");

            default:
                return Problem(result);
        }
    }

    // The user's retry of a Failed turn: a new attempt of the same message (no new message). Idempotent on a
    // Completed turn (returns its reply).
    public static async Task<Results<Ok<AssistantTurnResponse>, ProblemHttpResult>> RetryMessageAsync(
        Guid id,
        AuthenticatedUser user,
        AssistantTurnProcessor processor,
        CancellationToken cancellationToken)
    {
        var result = await processor.RetryAsync(user.UserId, id, cancellationToken);

        return result.Status switch
        {
            AssistantResultStatus.Ok => TypedResults.Ok(ToResponse(result.Value!)),
            AssistantResultStatus.NotFound => TypedResults.Problem(title: "Messaggio non trovato.", detail: MessageNotFoundMessage,
                statusCode: StatusCodes.Status404NotFound, extensions: Code(AssistantErrorCodes.MessageNotFound)),
            _ => Problem(result)
        };
    }

    // ---- Mapping ----

    internal static AssistantConversationResponse ToResponse(AssistantConversation conversation) =>
        new(conversation.Id, conversation.ClientConversationId, conversation.CreatedAtUtc, conversation.UpdatedAtUtc);

    internal static AssistantMessageResponse ToResponse(AssistantMessage message) =>
        new(
            message.Id,
            message.ConversationId,
            message.Sequence,
            message.Role.ToString(),
            message.Content,
            message.ClientMessageId,
            message.ReplyToMessageId,
            message.TurnStatus is { } status ? new AssistantTurnStateResponse(status.ToString(), message.TurnAttempts!.Value, message.FailureCode) : null,
            message.CreatedAtUtc);

    internal static AssistantTurnResponse ToResponse(AssistantTurn turn) =>
        new(ToResponse(turn.UserMessage), turn.Reply is { } reply ? ToResponse(reply) : null, turn.Replayed);

    // ---- Cursor ----

    // Opaque to clients: "<UpdatedAt UtcTicks>_<id>" of the previous page's last conversation.
    internal static string FormatCursor(AssistantConversationCursor cursor) =>
        string.Create(CultureInfo.InvariantCulture, $"{cursor.UpdatedAtUtc.UtcTicks}_{cursor.Id:N}");

    internal static bool TryParseCursor(string text, out AssistantConversationCursor? cursor)
    {
        cursor = null;
        var parts = text.Split('_');

        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            return false;
        }

        cursor = new AssistantConversationCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }

    // ---- Results ----

    // NotFound (of a conversation), InProgress, Conflict and Busy. Retryable: turn_in_progress (later) and
    // assistant_busy (at once, with the same request).
    private static ProblemHttpResult Problem<T>(AssistantResult<T> result) =>
        result.Status switch
        {
            AssistantResultStatus.NotFound => ConversationNotFound(),
            AssistantResultStatus.InProgress => TypedResults.Problem(
                title: "Messaggio in elaborazione.",
                detail: TurnInProgressMessage,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = AssistantErrorCodes.TurnInProgress,
                    ["messageId"] = (result.Value as AssistantTurn)?.UserMessage.Id
                }),
            AssistantResultStatus.Conflict when result.Code == AssistantErrors.ClientMessageIdReused => TypedResults.Problem(
                title: "Identificativo già usato.", detail: ClientMessageIdReusedMessage,
                statusCode: StatusCodes.Status409Conflict, extensions: Code(AssistantErrorCodes.ClientMessageIdReused)),
            AssistantResultStatus.Conflict when result.Code == AssistantErrors.TurnSuperseded => TypedResults.Problem(
                title: "Nuovo tentativo non consentito.", detail: TurnSupersededMessage,
                statusCode: StatusCodes.Status409Conflict, extensions: Code(AssistantErrorCodes.TurnSuperseded)),
            // Busy, and any refusal not expected from these endpoints: temporary, never internal details.
            _ => TypedResults.Problem(
                title: "Conversazione occupata.", detail: BusyMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable, extensions: Code(AssistantErrorCodes.Busy))
        };

    private static ProblemHttpResult ConversationNotFound() =>
        TypedResults.Problem(title: "Conversazione non trovata.", detail: ConversationNotFoundMessage,
            statusCode: StatusCodes.Status404NotFound, extensions: Code(AssistantErrorCodes.ConversationNotFound));

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] },
            title: "Richiesta non valida.", extensions: Code(AssistantErrorCodes.InvalidRequest));

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };
}
