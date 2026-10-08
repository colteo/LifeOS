using LifeOS.Application.Persistence;
using LifeOS.Domain.Journal;

namespace LifeOS.Application.Journal;

// Title null or blank: no title. Updates replace all three fields.
public sealed record CreateJournalEntryCommand(DateTimeOffset OccurredAtUtc, string? Title, string Content);

public sealed record UpdateJournalEntryCommand(DateTimeOffset OccurredAtUtc, string? Title, string Content);

// The position after which the next page starts: the timeline is ordered by OccurredAtUtc, then
// CreatedAtUtc, then Id, all descending, so a cursor is the last item's triple.
public sealed record JournalCursor(DateTimeOffset OccurredAtUtc, DateTimeOffset CreatedAtUtc, Guid Id)
{
    public static JournalCursor After(JournalEntry entry) => new(entry.OccurredAtUtc, entry.CreatedAtUtc, entry.Id);
}

// Next is null on the last page.
public sealed record JournalPage(IReadOnlyList<JournalEntry> Items, JournalCursor? Next);

public enum JournalResultStatus
{
    Ok,
    Invalid,
    NotFound
}

// The outcome of a journal write. Invalid carries the request field and a readable message.
public sealed record JournalResult(JournalResultStatus Status, JournalEntry? Entry = null, string? Field = null, string? Message = null)
{
    public static JournalResult Ok(JournalEntry entry) => new(JournalResultStatus.Ok, entry);

    public static JournalResult NotFound() => new(JournalResultStatus.NotFound);

    // Domain validation failures name the offending parameter; it is also the request field name.
    public static JournalResult Invalid(ArgumentException exception) =>
        new(JournalResultStatus.Invalid, Field: exception.ParamName ?? "request", Message: exception.Message);
}

// The user's journal, newest first, one bounded page at a time (keyset paging, so entries written
// while paging never shift or repeat rows).
public sealed class GetJournalEntriesHandler(IJournalEntryRepository repository)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    // Throws ArgumentOutOfRangeException when pageSize is not 1–MaxPageSize.
    public async Task<JournalPage> HandleAsync(Guid userId, JournalCursor? after, int pageSize, CancellationToken cancellationToken)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException("limit", pageSize, $"The page size must be between 1 and {MaxPageSize}.");
        }

        // One row more than the page tells whether another page exists.
        var items = await repository.GetPageAsync(userId, after, pageSize + 1, cancellationToken);

        if (items.Count <= pageSize)
        {
            return new JournalPage(items, null);
        }

        var page = items.Take(pageSize).ToList();

        return new JournalPage(page, JournalCursor.After(page[^1]));
    }
}

// Null for a missing entry or another user's.
public sealed class GetJournalEntryHandler(IJournalEntryRepository repository)
{
    public Task<JournalEntry?> HandleAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        repository.GetAsync(userId, id, cancellationToken);
}

// AI-004: the entry and its memory-index request are saved in one transaction (no AI call).
public sealed class CreateJournalEntryHandler(IJournalEntryRepository repository, IJournalIndexQueue indexQueue, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<JournalResult> HandleAsync(Guid userId, CreateJournalEntryCommand command, CancellationToken cancellationToken)
    {
        JournalEntry entry;

        try
        {
            entry = JournalEntry.Create(userId, command.OccurredAtUtc, command.Title, command.Content, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return JournalResult.Invalid(exception);
        }

        await unitOfWork.TryInTransactionAsync(async ct =>
        {
            await repository.AddAsync(entry, ct);
            await indexQueue.EnqueueAsync(userId, entry.Id, entry.UpdatedAtUtc, entry.UpdatedAtUtc, ct);
            return true;
        }, cancellationToken);

        return JournalResult.Ok(entry);
    }
}

// Replaces the occurred-at time, title and content, and stamps UpdatedAtUtc. AI-004: the change and
// the memory-index request for the new revision are saved in one transaction (no AI call).
public sealed class UpdateJournalEntryHandler(IJournalEntryRepository repository, IJournalIndexQueue indexQueue, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<JournalResult> HandleAsync(Guid userId, Guid id, UpdateJournalEntryCommand command, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(userId, id, cancellationToken) is not { } entry)
        {
            return JournalResult.NotFound();
        }

        try
        {
            entry.Update(command.OccurredAtUtc, command.Title, command.Content, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return JournalResult.Invalid(exception);
        }

        var saved = await unitOfWork.TryInTransactionAsync(async ct =>
        {
            if (!await repository.UpdateAsync(entry, ct))
            {
                return false;
            }

            await indexQueue.EnqueueAsync(userId, entry.Id, entry.UpdatedAtUtc, entry.UpdatedAtUtc, ct);
            return true;
        }, cancellationToken);

        return saved ? JournalResult.Ok(entry) : JournalResult.NotFound();
    }
}

// Hard delete (JRN-001). AI-004: the memory-index request and every derived chunk cascade with it.
public sealed class DeleteJournalEntryHandler(IJournalEntryRepository repository)
{
    public async Task<JournalResultStatus> HandleAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        await repository.DeleteAsync(userId, id, cancellationToken) ? JournalResultStatus.Ok : JournalResultStatus.NotFound;
}
