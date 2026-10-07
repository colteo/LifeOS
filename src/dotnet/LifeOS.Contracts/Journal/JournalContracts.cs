namespace LifeOS.Contracts.Journal;

// OccurredAtUtc (required): the instant the entry is about; any offset is accepted and stored as UTC.
// Title is optional (null or blank: no title). Content is required. There is no user id: the owner is
// always the authenticated user.
public sealed record CreateJournalEntryRequest(DateTimeOffset? OccurredAtUtc, string? Title, string? Content);

// Replaces all three fields: an omitted or blank Title removes the title.
public sealed record UpdateJournalEntryRequest(DateTimeOffset? OccurredAtUtc, string? Title, string? Content);

public sealed record JournalEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string? Title,
    string Content,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

// One page of the journal, newest first (OccurredAtUtc, then CreatedAtUtc, then Id). NextCursor
// continues the list (pass it as "cursor"); null on the last page.
public sealed record JournalEntryPageResponse(IReadOnlyList<JournalEntryResponse> Items, string? NextCursor);
