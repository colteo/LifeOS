namespace LifeOS.Application.Assistant;

// AI-007 F3: the seam between the turn processor (core, module-agnostic) and a module that acts on
// interpretations. v1 has one capability, Finance (FinanceAssistantCapability), which handles every intent;
// when a second module arrives the processor routes intents to capabilities, and the general intents
// (GeneralQuestion, Unsupported) move to the core. Until then no router exists (AGENTS.md: no speculative
// abstraction).

// The conversation's open operation as the turn read it before interpreting: the draft version the
// interpretation is applied to. A change of either (an edit, a confirmation, a cancellation, an expiry, a
// new operation) means the interpretation answers a draft that no longer exists.
public sealed record AssistantOperationSnapshot(Guid OperationId, int DraftVersion);

// What a turn needs before calling the interpreter: the minimal, id-free context sent to the model and the
// snapshot that fences the application of its answer.
public sealed record AssistantTurnPreparation(AssistantInterpretationContext Context, AssistantOperationSnapshot? OpenOperation);

// Either the reply to store (the turn completes) or a stable failure code (the turn fails, nothing of the
// capability's work is kept: it ran inside the turn's unit of work, which is rolled back).
public sealed record AssistantCapabilityOutcome(string? Reply, string? FailureCode)
{
    public static AssistantCapabilityOutcome Answered(string reply) => new(reply, null);

    public static AssistantCapabilityOutcome Failed(string failureCode) => new(null, failureCode);
}

public interface IAssistantCapability
{
    // Reads only (an outlived draft may be marked Expired, as every operation read does). Called outside any
    // database transaction, before the interpreter.
    Task<AssistantTurnPreparation> PrepareAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    // Called inside the turn's unit of work, after the turn's lock (the attempt still holds the turn and no
    // later message exists): validates and resolves the interpretation against the user's data, applies it
    // through the module's use cases if `preparation.OpenOperation` is still the open operation, and returns
    // the reply. It never confirms an operation and never writes the module's records.
    Task<AssistantCapabilityOutcome> ApplyAsync(Guid userId, Guid conversationId, AssistantTurnPreparation preparation,
        AssistantInterpretation interpretation, CancellationToken cancellationToken);
}
