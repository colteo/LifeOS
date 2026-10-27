namespace LifeOS.Application.Assistant;

// AI-007 F3: the seam between the turn processor (core, module-agnostic) and what acts on interpretations.
// AI-008: the processor still sees one capability, AssistantCapabilityRouter, which routes every interpretation to
// the module that owns it (IAssistantModule: Finance, Hydration, Meals) and answers the general intents itself.

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

// The conversation's open draft as a module describes it: the snapshot that fences a turn, and what the
// interpreter may know about it (kind, filled/missing field names, the field asked last; never values or ids).
public sealed record AssistantOpenDraft(AssistantOperationSnapshot Snapshot, AssistantOpenDraftContext Context);

// AI-008: one module of the assistant. It owns one operation kind and the intents that open it; it describes its
// open draft, adds its part of the interpreter's context, and applies the interpretations routed to it through its
// own use cases. A module never confirms nor cancels from text, never writes its records outside the confirmation
// endpoint, and reads only its own section of the interpretation.
public interface IAssistantModule
{
    // The operation kind it owns (AssistantOperationKinds).
    string Kind { get; }

    // What the module records, for the general replies ("spese ed entrate", "l'acqua", "i pasti").
    string Label { get; }

    // What one of its drafts is, for the reply when another draft is open ("transazione", "pasto").
    string DraftName { get; }

    // Whether the intent opens a new draft of this module.
    bool Opens(AssistantIntent intent);

    // The conversation's open operation if it is of this kind and still open (an outlived one is marked Expired).
    Task<AssistantOpenDraft?> GetOpenDraftAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    // This module's part of the interpreter's context, given the conversation's open draft (of any kind).
    Task ContributeAsync(Guid userId, AssistantOpenDraft? open, AssistantContextSections context, CancellationToken cancellationToken);

    // Inside the turn's unit of work, after the router checked that the open draft is still the one interpreted:
    // an intent that opens this module's draft (open is null or of another kind only when the router allows it),
    // or a draft intent (update, confirm, cancel, clarification) while this module's draft is open.
    Task<AssistantCapabilityOutcome> ApplyAsync(Guid userId, Guid conversationId, AssistantOpenDraft? open, AssistantInterpretation interpretation,
        CancellationToken cancellationToken);
}

// The module-specific parts of the context, filled by the modules (Finance: the category names).
public sealed class AssistantContextSections
{
    public AssistantFinanceContext Finance { get; set; } = AssistantFinanceContext.None;
}
