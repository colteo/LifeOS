using LifeOS.Application.Assistant;
using LifeOS.Domain.Finance.Assistant;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Assistant;

// A problem found while resolving a turn, to be asked back instead of applied. Mention: what the user wrote
// (shown back in «»); Account fields: the resolved account that does not fit the stated currency.
public sealed record FinanceTurnIssue(string Code, string? Mention = null, string? AccountName = null, string? AccountCurrency = null,
    string? StatedCurrency = null);

public static class FinanceTurnIssues
{
    public const string AmountInvalid = "amount_invalid";
    public const string AccountAmbiguous = "account_ambiguous";
    public const string AccountNotFound = "account_not_found";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string CategoryAmbiguous = "category_ambiguous";
    public const string CategoryNotFound = "category_not_found";
    public const string DateInvalid = "date_invalid";
    public const string TimeZoneRequired = "time_zone_required";

    // The model marked the field ambiguous (Mention = the field name).
    public const string FieldAmbiguous = "field_ambiguous";
}

// AI-007 F3 replies, Italian, deterministic templates (D-5 as proposed): the model's text is never shown.
// Product owner's style: during an operation, short and direct ("Da quale conto?", "Quale categoria?",
// "Ho preparato la spesa. Vuoi modificarla?"); for questions, more explanatory — but v1 cannot read the
// user's data to answer them and says so, without inventing balances, totals or anything not retrieved.
// Replies are built from the stored draft (the view), never from the model's output.
public static class FinanceAssistantReplies
{
    public const string ConfirmWithButton = "La transazione è pronta. Confermala con il pulsante.";
    public const string NothingToConfirm = "Non c'è nessuna transazione da confermare.";
    public const string CancelWithButton = "Per annullare la bozza usa il pulsante Annulla.";
    public const string NothingToCancel = "Non c'è nessuna bozza da annullare.";
    public const string AlreadyOpen = "C'è già una transazione in preparazione. Completala o annullala prima di iniziarne un'altra.";
    public const string NothingToUpdate = "Non c'è nessuna transazione in preparazione. Dimmi cosa vuoi registrare.";

    public const string GeneralQuestion =
        "Per ora posso aiutarti solo a registrare spese ed entrate. Non posso ancora consultare saldi, movimenti o altri dati di LifeOS, " +
        "quindi non posso rispondere a questa domanda.";

    public const string NotUnderstood = "Non ho capito. Puoi riformulare? Ad esempio: «Ho speso 5 euro per la colazione».";

    public static string Unsupported(AssistantUnsupportedReason reason) => reason switch
    {
        AssistantUnsupportedReason.Transfer => "Non posso ancora registrare trasferimenti tra conti: puoi farlo dalla sezione Finanze.",
        AssistantUnsupportedReason.MultipleTransactions => "Posso registrare una transazione alla volta. Iniziamo dalla prima: dimmi importo e motivo.",
        _ => "Per ora posso aiutarti solo a registrare spese ed entrate."
    };

    // After a draft was created or changed (or could not take a value): the first issue, otherwise the next
    // question, otherwise the ready message.
    public static string AfterChange(FinanceOperationView view, FinanceTurnIssue? issue) =>
        issue is not null ? Issue(issue, view.Operation.Draft.Type) : Next(view);

    // "Confermo" in chat: never a confirmation.
    public static string ConfirmRequested(FinanceOperationView? open) => open switch
    {
        null => NothingToConfirm,
        { CanConfirm: true } => ConfirmWithButton,
        _ => "Non è ancora pronta. " + Next(open)
    };

    // The use case refused a reference the resolution had accepted (e.g. deleted meanwhile): nothing changed.
    public static string Rejected(string? code, TransactionType? type) => code switch
    {
        FinanceOperationErrors.AccountNotFound or FinanceOperationErrors.CurrencyMismatch => "Non posso usare quel conto. " + AccountQuestion(type),
        FinanceOperationErrors.CategoryNotFound or FinanceOperationErrors.CategoryIncompatible => "Non posso usare quella categoria. Quale categoria?",
        _ => NotUnderstood
    };

    public static string NotUnderstoodWith(FinanceOperationView? open) => open is null ? NotUnderstood : "Non ho capito. " + Next(open);

    // The next thing the draft needs, or that it is ready.
    public static string Next(FinanceOperationView view)
    {
        var draft = view.Operation.Draft;

        if (view.Issues.FirstOrDefault(issue => issue != FinanceOperationErrors.TimeZoneRequired) is { } stale)
        {
            return stale switch
            {
                FinanceOperationErrors.AccountNotFound => "Il conto scelto non esiste più. " + AccountQuestion(draft.Type),
                FinanceOperationErrors.CurrencyMismatch => "Il conto scelto non è nella valuta indicata. " + AccountQuestion(draft.Type),
                FinanceOperationErrors.CategoryNotFound => "La categoria scelta non esiste più. Quale categoria?",
                FinanceOperationErrors.CategoryIncompatible => "La categoria non è adatta a questo tipo di transazione. Quale categoria?",
                _ => "Controlla la bozza prima di confermarla."
            };
        }

        if (draft.MissingFields.Count > 0)
        {
            return Question(draft.MissingFields[0], draft.Type);
        }

        var ready = draft.Type == TransactionType.Income ? "Ho preparato l'entrata. Vuoi modificarla?" : "Ho preparato la spesa. Vuoi modificarla?";

        return view.OccursInFuture ? ready + " La data è nel futuro: controllala prima di confermare." : ready;
    }

    private static string Question(string missingField, TransactionType? type) => missingField switch
    {
        FinanceDraftFields.Type => "È una spesa o un'entrata?",
        FinanceDraftFields.Amount => AmountQuestion(type),
        FinanceDraftFields.Account => AccountQuestion(type),
        FinanceDraftFields.Category => "Quale categoria?",
        _ => "Imposta il fuso orario nelle impostazioni per completare la data."
    };

    private static string Issue(FinanceTurnIssue issue, TransactionType? type) => issue.Code switch
    {
        FinanceTurnIssues.AmountInvalid => "L'importo non è valido. " + AmountQuestion(type),
        FinanceTurnIssues.AccountAmbiguous => $"Ho trovato più conti «{issue.Mention}». Quale intendi?",
        FinanceTurnIssues.AccountNotFound => $"Non trovo il conto «{issue.Mention}». " + AccountQuestion(type),
        FinanceTurnIssues.CurrencyMismatch =>
            $"Il conto «{issue.AccountName}» è in {issue.AccountCurrency}, non in {issue.StatedCurrency}. Quale conto vuoi usare?",
        FinanceTurnIssues.CategoryAmbiguous => $"Ho trovato più categorie «{issue.Mention}». Quale intendi?",
        FinanceTurnIssues.CategoryNotFound => $"Non trovo la categoria «{issue.Mention}». Quale categoria?",
        FinanceTurnIssues.DateInvalid => "La data non è valida. Quando è successo?",
        FinanceTurnIssues.TimeZoneRequired => "Imposta il fuso orario nelle impostazioni per usare date come «ieri».",
        _ => issue.Mention switch
        {
            AssistantFinanceFieldNames.Type => "È una spesa o un'entrata?",
            AssistantFinanceFieldNames.Amount => "Non ho capito l'importo. " + AmountQuestion(type),
            AssistantFinanceFieldNames.Account => "Non ho capito il conto. " + AccountQuestion(type),
            AssistantFinanceFieldNames.Category => "Non ho capito la categoria. Quale categoria?",
            AssistantFinanceFieldNames.Date or AssistantFinanceFieldNames.Time => "Non ho capito quando. Quando è successo?",
            _ => "Non ho capito un dettaglio. Puoi ripeterlo?"
        }
    };

    private static string AmountQuestion(TransactionType? type) => type switch
    {
        TransactionType.Expense => "Quanto hai speso?",
        TransactionType.Income => "Quanto hai ricevuto?",
        _ => "Qual è l'importo?"
    };

    private static string AccountQuestion(TransactionType? type) => type == TransactionType.Income ? "Su quale conto?" : "Da quale conto?";
}
