using LifeOS.Application.Persistence;
using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

// AI-007 §10.1: the only user-facing way into a turn, and the deterministic orchestrator of the turn. It
// accepts the message (Send) or the user's retry (Retry) — T1: from here the message is durable — and then
// processes, in the same request, the attempt that this request started:
//
//   1. prepare (reads only): the capability's minimal, id-free context and the open operation's snapshot
//      (id + draft version) that the answer will be applied to;
//   2. interpret, outside any database transaction, bounded by the turn's lease (the deadline leaves
//      CompletionMargin to store the outcome before the lease ends);
//   3. validate the interpretation (AssistantInterpretationValidator): an invalid one fails the turn;
//   4. T2, one unit of work: lock the turn first (TryLockTurnAsync: this attempt still holds it and no later
//      message exists — checked BEFORE any operational write, and held until the commit, so a concurrent send,
//      retry, recovery or delete waits), then the capability applies the interpretation through the module's
//      use cases (conditional on the snapshot's draft version), then the reply completes the turn. All of it
//      commits or nothing does: a draft is never changed by a turn that does not complete, and a reply never
//      exists for a draft change that was not stored.
//
// Failures close the attempt with a stable code and no assistant message (FailAssistantTurnHandler, fenced
// by the attempt): assistant_unavailable (the default: no interpreter is configured while ADR-014 is not
// accepted), assistant_timeout, assistant_invalid_output, assistant_refused, draft_changed (the draft moved
// while interpreting) and turn_superseded (a later message exists). A retry (same clientMessageId, or
// "Riprova") interprets again from the current draft; since a failed attempt changed nothing and a completed
// turn is only replayed, a retry never applies an interpretation twice.
//
// Nothing here confirms an operation: confirmations happen only through their explicit endpoint.
//
// Only attempts started by this request are processed. A replay that finds a Completed or Failed turn, or a
// turn Processing under another request's lease (InProgress), is returned as it is.
//
// The turn handlers are composed here rather than resolved from DI: the processor is the only component that
// may close an attempt, so CompleteAssistantTurnHandler and FailAssistantTurnHandler are not registered
// anywhere, and the HTTP adapter cannot reference any of them (AssistantArchitectureTests).
public sealed class AssistantTurnProcessor(
    IAssistantConversationRepository repository,
    IAssistantInterpreter interpreter,
    IAssistantCapability capability,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    // Kept between the interpreter's deadline and the end of the lease, to store the outcome (T2).
    public static readonly TimeSpan CompletionMargin = TimeSpan.FromSeconds(15);

    private readonly SendAssistantMessageHandler _send = new(repository, clock);
    private readonly RetryAssistantTurnHandler _retry = new(repository, clock);
    private readonly CompleteAssistantTurnHandler _complete = new(repository, clock);
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
        var preparation = await capability.PrepareAsync(userId, message.ConversationId, CancellationToken.None);
        var result = await InterpretAsync(message, preparation);

        if (result.Failure is { } failure)
        {
            return await FailAsync(userId, turn, failure switch
            {
                AssistantInterpretationFailure.Timeout => AssistantErrors.AssistantTimeout,
                AssistantInterpretationFailure.InvalidOutput => AssistantErrors.AssistantInvalidOutput,
                AssistantInterpretationFailure.Refused => AssistantErrors.AssistantRefused,
                _ => AssistantErrors.AssistantUnavailable
            });
        }

        if (AssistantInterpretationValidator.Validate(result.Interpretation, message.Content) is not null)
        {
            return await FailAsync(userId, turn, AssistantErrors.AssistantInvalidOutput);
        }

        return await CompleteAsync(userId, turn, preparation, result.Interpretation!);
    }

    private async Task<AssistantInterpretationResult> InterpretAsync(AssistantMessage message, AssistantTurnPreparation preparation)
    {
        var budget = message.TurnLeaseUntilUtc!.Value - CompletionMargin - clock.GetUtcNow();

        if (budget <= TimeSpan.Zero)
        {
            return AssistantInterpretationResult.Failed(AssistantInterpretationFailure.Timeout);
        }

        using var deadline = new CancellationTokenSource(budget, clock);

        try
        {
            return await interpreter.InterpretAsync(
                new AssistantInterpretationRequest(AssistantInterpretationProtocol.Version, message.Content, preparation.Context), deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return AssistantInterpretationResult.Failed(AssistantInterpretationFailure.Timeout);
        }
    }

    private async Task<AssistantResult<AssistantTurn>> CompleteAsync(Guid userId, AssistantTurn turn, AssistantTurnPreparation preparation,
        AssistantInterpretation interpretation)
    {
        var message = turn.UserMessage;
        var attempt = message.TurnAttempts!.Value;
        var locked = false;
        AssistantCapabilityOutcome? outcome = null;
        AssistantResult<AssistantTurn>? completed = null;

        var committed = await unitOfWork.TryInTransactionAsync(async cancellationToken =>
        {
            locked = await repository.TryLockTurnAsync(userId, message.ConversationId, message.Id, attempt, clock.GetUtcNow(), cancellationToken);

            if (!locked)
            {
                return false;
            }

            outcome = await capability.ApplyAsync(userId, message.ConversationId, preparation, interpretation, cancellationToken);

            if (outcome.Reply is not { } reply)
            {
                return false;
            }

            completed = await _complete.HandleAsync(userId, message.ConversationId, new CompleteAssistantTurnCommand(message.Id, attempt, reply),
                cancellationToken);

            return completed.Status == AssistantResultStatus.Ok;
        }, CancellationToken.None);

        if (committed)
        {
            return AssistantResult<AssistantTurn>.Ok(completed!.Value! with { Replayed = turn.Replayed });
        }

        // The attempt no longer holds the turn (recovered, retried, deleted: Fail sorts it out) or a later
        // message exists: the attempt is failed if it still holds the turn, and nothing was applied.
        if (!locked)
        {
            return await FailAsync(userId, turn, AssistantErrors.TurnSuperseded);
        }

        return await FailAsync(userId, turn, outcome?.FailureCode ?? AssistantErrors.AssistantInvalidOutput);
    }

    private async Task<AssistantResult<AssistantTurn>> FailAsync(Guid userId, AssistantTurn turn, string failureCode)
    {
        var message = turn.UserMessage;
        var failed = await _fail.HandleAsync(userId, message.ConversationId,
            new FailAssistantTurnCommand(message.Id, message.TurnAttempts!.Value, failureCode), CancellationToken.None);

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
