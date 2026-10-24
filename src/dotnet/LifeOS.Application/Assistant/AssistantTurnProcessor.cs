using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

// AI-007 F1 §10.1: the only user-facing way into a turn. It accepts the message (Send) or the user's retry
// (Retry) — T1: from here the message is durable — and then processes, in the same request, the attempt
// that this request started.
//
// F1 has no interpreter (no AI provider is approved: ADR-014), so processing has exactly one outcome: the
// attempt fails with assistant_unavailable through FailAssistantTurnHandler. No assistant message is
// stored, no turn is Completed, and no Processing turn is left behind waiting for an answer that nothing
// would produce. The user message stays in the conversation, Failed, and can be retried.
//
// F3 replaces that single outcome with the interpreter call (outside any database transaction) followed by
// CompleteAssistantTurnHandler or FailAssistantTurnHandler (T2) for the same attempt — the fencing token
// returned by Send/Retry — so the HTTP contract does not change: a Failed turn then carries the provider's
// failure code instead of assistant_unavailable.
//
// Only attempts started by this request are processed. A replay that finds a Completed or Failed turn, or
// a turn Processing under another request's lease (InProgress), is returned as it is.
//
// The turn handlers are composed here rather than resolved from DI: the processor is the only component
// that may close an attempt, so FailAssistantTurnHandler (and, in F3, CompleteAssistantTurnHandler) is not
// registered anywhere, and the HTTP adapter cannot reference any of them (AssistantArchitectureTests).
public sealed class AssistantTurnProcessor(IAssistantConversationRepository repository, TimeProvider clock)
{
    private readonly SendAssistantMessageHandler _send = new(repository, clock);
    private readonly RetryAssistantTurnHandler _retry = new(repository, clock);
    private readonly FailAssistantTurnHandler _fail = new(repository, clock);

    public async Task<AssistantResult<AssistantTurn>> SendAsync(Guid userId, Guid conversationId, SendAssistantMessageCommand command,
        CancellationToken cancellationToken) =>
        await ProcessAsync(userId, await _send.HandleAsync(userId, conversationId, command, cancellationToken));

    public async Task<AssistantResult<AssistantTurn>> RetryAsync(Guid userId, Guid messageId, CancellationToken cancellationToken) =>
        await ProcessAsync(userId, await _retry.HandleAsync(userId, messageId, cancellationToken));

    // Not cancellable: once the message is stored, closing its attempt must not depend on the client still
    // waiting for the answer (otherwise the turn would stay Processing until its lease ends).
    private async Task<AssistantResult<AssistantTurn>> ProcessAsync(Guid userId, AssistantResult<AssistantTurn> accepted)
    {
        if (accepted is not { Status: AssistantResultStatus.Ok, Value: { UserMessage.TurnStatus: AssistantTurnStatus.Processing } turn })
        {
            return accepted;
        }

        var message = turn.UserMessage;
        var failed = await _fail.HandleAsync(userId, message.ConversationId,
            new FailAssistantTurnCommand(message.Id, message.TurnAttempts!.Value, AssistantErrors.AssistantUnavailable), CancellationToken.None);

        return failed.Status switch
        {
            AssistantResultStatus.Ok => AssistantResult<AssistantTurn>.Ok(failed.Value! with { Replayed = turn.Replayed }),

            // The conversation was deleted in the meantime.
            AssistantResultStatus.NotFound => failed,

            // The attempt no longer holds the turn (it was recovered or retried by another request) or the
            // writes kept losing: the turn's state changed under this request. Repeating the same request is
            // safe and returns that state.
            _ => AssistantResult<AssistantTurn>.Busy()
        };
    }
}
