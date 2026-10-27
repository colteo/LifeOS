namespace LifeOS.Application.Assistant;

// AI-008: the one capability the turn processor sees. It routes every validated interpretation to the module that
// owns it, deterministically — the model only names an intent from a closed list; it never chooses a module, an
// endpoint or a command:
//
//   general_question, unsupported                    → the core's fixed replies (nothing is read or written);
//   draft_update / draft_confirm / draft_cancel /     → the module of the conversation's open draft, whatever its
//   clarification with an open draft                    kind; without an open draft, the core's replies;
//   an intent that opens a draft                     → the module that owns it, if enabled; while a draft of
//                                                       another kind is open, the core asks to finish it first
//                                                       (one open operation per conversation); an intent of a
//                                                       module that is not enabled gets "not available".
//
// Fencing (as AI-007 F3): ApplyAsync reads the open draft again inside the turn's unit of work and applies nothing
// unless it is still the one the interpretation was made for (same id and draft version): draft_changed.
public sealed class AssistantCapabilityRouter(IAssistantOperationReader operations, IEnumerable<IAssistantModule> modules) : IAssistantCapability
{
    private readonly IReadOnlyList<IAssistantModule> _modules = modules.ToList();

    public async Task<AssistantTurnPreparation> PrepareAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var open = await OpenDraftAsync(userId, conversationId, cancellationToken);
        var sections = new AssistantContextSections();

        foreach (var module in _modules)
        {
            await module.ContributeAsync(userId, open?.Draft, sections, cancellationToken);
        }

        var context = new AssistantInterpretationContext(AssistantInterpretationProtocol.Locale, open?.Draft.Context, sections.Finance);

        return new AssistantTurnPreparation(context, open?.Draft.Snapshot);
    }

    public async Task<AssistantCapabilityOutcome> ApplyAsync(Guid userId, Guid conversationId, AssistantTurnPreparation preparation,
        AssistantInterpretation interpretation, CancellationToken cancellationToken)
    {
        var open = await OpenDraftAsync(userId, conversationId, cancellationToken);

        if (open?.Draft.Snapshot != preparation.OpenOperation)
        {
            return AssistantCapabilityOutcome.Failed(AssistantErrors.DraftChanged);
        }

        var replies = new AssistantCoreReplies(_modules.Select(module => module.Label).ToList());

        switch (interpretation.Intent)
        {
            case AssistantIntent.GeneralQuestion:
                return AssistantCapabilityOutcome.Answered(replies.GeneralQuestion);

            case AssistantIntent.Unsupported:
                return AssistantCapabilityOutcome.Answered(replies.Unsupported(interpretation.UnsupportedReason!.Value));

            case AssistantIntent.Clarification or AssistantIntent.DraftUpdate or AssistantIntent.DraftConfirm or AssistantIntent.DraftCancel:
                return open is { } current
                    ? await current.Module.ApplyAsync(userId, conversationId, current.Draft, interpretation, cancellationToken)
                    : AssistantCapabilityOutcome.Answered(interpretation.Intent switch
                    {
                        AssistantIntent.DraftUpdate => AssistantCoreReplies.NothingToUpdate,
                        AssistantIntent.DraftConfirm => AssistantCoreReplies.NothingToConfirm,
                        AssistantIntent.DraftCancel => AssistantCoreReplies.NothingToCancel,
                        _ => AssistantCoreReplies.NotUnderstood
                    });
        }

        if (_modules.FirstOrDefault(module => module.Opens(interpretation.Intent)) is not { } owner)
        {
            return AssistantCapabilityOutcome.Answered(replies.NotAvailable);
        }

        if (open is { } other && other.Module != owner)
        {
            return AssistantCapabilityOutcome.Answered(AssistantCoreReplies.OtherDraftOpen(other.Module.DraftName));
        }

        // The owner answers a second draft of its own kind itself (it knows how to describe it).
        return await owner.ApplyAsync(userId, conversationId, open?.Draft, interpretation, cancellationToken);
    }

    // The conversation's open draft and its module. A kind without an enabled module (its module was switched off
    // while one of its drafts was open) is still described by nobody: the conversation then behaves as if it had no
    // draft for the interpreter, and creating another one is refused by the one-open-operation rule.
    private async Task<(IAssistantModule Module, AssistantOpenDraft Draft)?> OpenDraftAsync(Guid userId, Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (await operations.GetOpenAsync(userId, conversationId, cancellationToken) is not { } header
            || _modules.FirstOrDefault(module => module.Kind == header.Kind) is not { } module
            || await module.GetOpenDraftAsync(userId, conversationId, cancellationToken) is not { } draft)
        {
            return null;
        }

        return (module, draft);
    }
}

// The core's replies, Italian, deterministic templates (AI-007 D-5): the model's text is never shown. They name
// only the enabled modules (with Finance alone, the texts of AI-007 v1).
public sealed class AssistantCoreReplies(IReadOnlyList<string> labels)
{
    public const string NothingToConfirm = "Non c'è nessuna bozza da confermare.";
    public const string NothingToCancel = "Non c'è nessuna bozza da annullare.";
    public const string NothingToUpdate = "Non c'è nessuna bozza in preparazione. Dimmi cosa vuoi registrare.";
    public const string NotUnderstood = "Non ho capito. Puoi riformulare? Ad esempio: «Ho speso 5 euro per la colazione».";

    public string Capabilities => "Per ora posso aiutarti solo a registrare " + Join(labels) + ".";

    public string GeneralQuestion =>
        Capabilities[..^1] + ". Non posso ancora consultare saldi, movimenti o altri dati di LifeOS, quindi non posso rispondere a questa domanda.";

    public string NotAvailable => "Questa funzione non è ancora disponibile nell'assistente. " + Capabilities;

    public string Unsupported(AssistantUnsupportedReason reason) => reason switch
    {
        AssistantUnsupportedReason.Transfer => "Non posso ancora registrare trasferimenti tra conti: puoi farlo dalla sezione Finanze.",
        AssistantUnsupportedReason.MultipleOperations => "Posso registrare una cosa alla volta. Iniziamo dalla prima: dimmi di cosa si tratta.",
        AssistantUnsupportedReason.OtherCapability => NotAvailable,
        _ => Capabilities
    };

    public static string OtherDraftOpen(string draftName) =>
        $"Hai già una bozza aperta ({draftName}). Confermala o annullala prima di iniziarne un'altra.";

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "nulla",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " e " + items[^1]
    };
}
