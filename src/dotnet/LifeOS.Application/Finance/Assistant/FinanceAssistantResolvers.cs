using System.Globalization;
using System.Text;
using LifeOS.Application.Assistant;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Assistant;

// AI-007 F3: deterministic resolution of what the model mentioned. A mention is text, never an id: it is
// matched only against the user's own accounts and categories (read through the user-scoped repositories),
// and the result is one id, "ambiguous" or "not found" — never the first candidate, never a default.

public enum FinanceMentionMatch
{
    Resolved,

    // Several of the user's resources match: the user chooses.
    Ambiguous,

    NotFound
}

public sealed record FinanceMentionResolution<T>(FinanceMentionMatch Match, T? Value) where T : class
{
    public static FinanceMentionResolution<T> NotFound { get; } = new(FinanceMentionMatch.NotFound, null);

    public static FinanceMentionResolution<T> Ambiguous { get; } = new(FinanceMentionMatch.Ambiguous, null);
}

public static class FinanceMentionResolver
{
    // Leading words that introduce an account in Italian ("dal conto Revolut", "con la carta Visa"). They are
    // dropped from the mention, never from the account names, and never down to an empty mention.
    private static readonly HashSet<string> AccountLeadWords =
        ["il", "lo", "la", "l", "i", "le", "dal", "dallo", "dalla", "sul", "sullo", "sulla", "con", "col", "da", "di", "su", "in", "nel", "nella",
         "mio", "mia", "conto", "carta"];

    // An account by the user's mention: names compared without case, accents and punctuation; an exact name
    // first; otherwise the mention as whole consecutive words of a name ("Revolut" for "Revolut Personale").
    // One match → that account; several (same-name accounts, or "Revolut" for "Revolut EUR" and "Revolut
    // USD") → Ambiguous; none → NotFound. There are no aliases in Finance, so none are used. The user having a
    // single account changes nothing: without a mention this resolver is not called at all.
    public static FinanceMentionResolution<Account> Account(string mention, IReadOnlyList<Account> accounts)
    {
        var words = Words(mention);

        // The mention as written may be a name itself ("Conto Revolut").
        if (Match(words, accounts, account => account.Name, allowPartial: false) is { Match: not FinanceMentionMatch.NotFound } asWritten)
        {
            return asWritten;
        }

        while (words.Length > 1 && AccountLeadWords.Contains(words[0]))
        {
            words = words[1..];
        }

        return Match(words, accounts, account => account.Name, allowPartial: true);
    }

    // A category by name among the user's categories compatible with the transaction type (any type while it
    // is unknown). A category the user stated may match partially (whole words); a model suggestion must name
    // one category exactly (the model was given the names): anything else is no suggestion at all.
    public static FinanceMentionResolution<Category> Category(string mention, TransactionType? type, IReadOnlyList<Category> categories,
        bool allowPartial)
    {
        var compatible = categories
            .Where(category => type is not { } t || TransactionInputResolver.CategoryCompatibilityError(t, category.CategoryType) is null)
            .ToList();

        return Match(Words(mention), compatible, category => category.Name, allowPartial);
    }

    // AI-007 F3-B2: a category the model marked as stated, i.e. as the user's choice. Live run 1 showed the model returning a
    // description as one ("45 euro di visita medica" → "visita medica"); whether the user chose or described cannot be told
    // deterministically, so the user's own words decide how far a mention may reach, and anything less than certain is asked:
    //   - the exact name of a compatible category: the user's choice (one), or ambiguous (several);
    //   - part of a category's name ("viaggi" for "Viaggi lavoro"): the user's choice only when the span has the word
    //     "categoria"/"categorie" ("categoria viaggi"); otherwise unclear, asked and never applied as theirs: a description
    //     ("rimborso spese") must not become the user's choice of "Rimborso spese viaggio";
    //   - nothing: not found, asked.
    // The result is a category or the issue to ask; a stated mention is never turned into a suggestion or into a note.
    public static (Category? Category, string? Issue) StatedCategory(string mention, string? span, TransactionType? type,
        IReadOnlyList<Category> categories)
    {
        var exact = Category(mention, type, categories, allowPartial: false);

        if (exact.Match != FinanceMentionMatch.NotFound)
        {
            return exact.Match == FinanceMentionMatch.Resolved ? (exact.Value, null) : (null, FinanceTurnIssues.CategoryAmbiguous);
        }

        var partial = Category(mention, type, categories, allowPartial: true);

        return partial.Match switch
        {
            FinanceMentionMatch.NotFound => (null, FinanceTurnIssues.CategoryNotFound),
            _ when !NamesACategory(span) => (null, FinanceTurnIssues.CategoryUnclear),
            FinanceMentionMatch.Resolved => (partial.Value, null),
            _ => (null, FinanceTurnIssues.CategoryAmbiguous)
        };
    }

    // The user wrote the word "categoria" (or "categorie") in the quoted text: an explicit category choice.
    public static bool NamesACategory(string? span) => Words(span).Any(word => word is "categoria" or "categorie");

    // Lowercase, without accents, letters and digits only, split in words.
    public static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var builder = new StringBuilder(text.Length);

        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static FinanceMentionResolution<T> Match<T>(string[] words, IReadOnlyList<T> candidates, Func<T, string> name, bool allowPartial)
        where T : class
    {
        if (words.Length == 0)
        {
            return FinanceMentionResolution<T>.NotFound;
        }

        var named = candidates.Select(candidate => (Candidate: candidate, Words: Words(name(candidate)))).ToList();
        var exact = named.Where(entry => entry.Words.SequenceEqual(words)).Select(entry => entry.Candidate).ToList();

        if (exact.Count > 0 || !allowPartial)
        {
            return Single(exact);
        }

        return Single(named.Where(entry => ContainsRun(entry.Words, words)).Select(entry => entry.Candidate).ToList());
    }

    private static FinanceMentionResolution<T> Single<T>(List<T> matches) where T : class => matches.Count switch
    {
        0 => FinanceMentionResolution<T>.NotFound,
        1 => new FinanceMentionResolution<T>(FinanceMentionMatch.Resolved, matches[0]),
        _ => FinanceMentionResolution<T>.Ambiguous
    };

    private static bool ContainsRun(string[] name, string[] words)
    {
        for (var start = 0; start + words.Length <= name.Length; start++)
        {
            if (name.AsSpan(start, words.Length).SequenceEqual(words))
            {
                return true;
            }
        }

        return false;
    }
}

public enum FinanceDateProblem
{
    // The user has no valid time zone: "ieri" cannot be computed (no zone is ever assumed).
    TimeZoneRequired,

    // A calendar date that does not exist (31/02) or lies outside the draft's range.
    InvalidDate
}

// The date the user meant, computed on the backend from the model's date expression, the user's time zone and
// the current instant (TimeProvider) — the model never supplies a date. Days are counted on the user's local
// calendar, so a DST change does not move "ieri"; the time of day is then resolved by
// FinanceDraftOccurrenceResolver (today without a time = now, another day = 12:00, DST rules). AI-008: the
// computation is shared with the other modules (AssistantDateExpressionResolver), within the draft's range.
public static class FinanceDateExpressionResolver
{
    public static (DateOnly? Date, FinanceDateProblem? Problem) Resolve(AssistantDateField field, string? timeZoneId, DateTimeOffset now)
    {
        var (date, problem) = AssistantDateExpressionResolver.Resolve(field, timeZoneId, now,
            Domain.Finance.Assistant.FinanceTransactionDraft.MinOccurredOn, Domain.Finance.Assistant.FinanceTransactionDraft.MaxOccurredOn);

        return (date, problem switch
        {
            null => null,
            AssistantDateProblem.TimeZoneRequired => FinanceDateProblem.TimeZoneRequired,
            _ => FinanceDateProblem.InvalidDate
        });
    }
}
