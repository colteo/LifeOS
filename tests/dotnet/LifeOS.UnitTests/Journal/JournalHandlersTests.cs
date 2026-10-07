using LifeOS.Application.Journal;
using LifeOS.Domain.Journal;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Journal;

public class JournalHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryJournalEntryRepository _repository = new();
    private readonly ManualTimeProvider _clock = new(Now);

    private Task<JournalResult> CreateAsync(Guid user, DateTimeOffset occurredAt, string content, string? title = null) =>
        new CreateJournalEntryHandler(_repository, _clock)
            .HandleAsync(user, new CreateJournalEntryCommand(occurredAt, title, content), CancellationToken.None);

    private async Task<JournalEntry> EntryAsync(Guid user, DateTimeOffset occurredAt, string content)
    {
        var result = await CreateAsync(user, occurredAt, content);
        Assert.Equal(JournalResultStatus.Ok, result.Status);
        return result.Entry!;
    }

    private Task<JournalResult> UpdateAsync(Guid user, Guid id, DateTimeOffset occurredAt, string content, string? title = null) =>
        new UpdateJournalEntryHandler(_repository, _clock)
            .HandleAsync(user, id, new UpdateJournalEntryCommand(occurredAt, title, content), CancellationToken.None);

    private Task<JournalPage> PageAsync(Guid user, JournalCursor? after = null, int size = 20) =>
        new GetJournalEntriesHandler(_repository).HandleAsync(user, after, size, CancellationToken.None);

    // ---- Create / get ----

    [Fact]
    public async Task Create_StoresTheEntryForTheCaller()
    {
        var result = await CreateAsync(TestUsers.A, Day, "  Pensieri del giorno  ", "  Lunedì ");

        Assert.Equal(JournalResultStatus.Ok, result.Status);
        var stored = Assert.Single(_repository.Entries);
        Assert.Equal((TestUsers.A, Day, "Lunedì", "Pensieri del giorno", Now, Now),
            (stored.UserId, stored.OccurredAtUtc, stored.Title, stored.Content, stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal(stored.Id, result.Entry!.Id);
    }

    [Fact]
    public async Task Create_WithoutTitle_IsAllowed()
    {
        var result = await CreateAsync(TestUsers.A, Day, "Solo testo");

        Assert.Equal(JournalResultStatus.Ok, result.Status);
        Assert.Null(Assert.Single(_repository.Entries).Title);
    }

    [Theory]
    [InlineData("", null, "content")]
    [InlineData("   ", null, "content")]
    [InlineData("Testo", "title-too-long", "title")]
    public async Task Create_InvalidInput_IsInvalid_WithTheField_AndStoresNothing(string content, string? title, string field)
    {
        var result = await CreateAsync(TestUsers.A, Day, content, title == "title-too-long" ? new string('t', JournalEntry.TitleMaxLength + 1) : title);

        Assert.Equal(JournalResultStatus.Invalid, result.Status);
        Assert.Equal(field, result.Field);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Empty(_repository.Entries);
    }

    [Fact]
    public async Task Create_InvalidOccurredAt_IsInvalid()
    {
        var result = await CreateAsync(TestUsers.A, default, "Testo");

        Assert.Equal((JournalResultStatus.Invalid, "occurredAtUtc"), (result.Status, result.Field));
        Assert.Empty(_repository.Entries);
    }

    [Fact]
    public async Task Get_IsOwnerOnly()
    {
        var entry = await EntryAsync(TestUsers.A, Day, "Privato");
        var handler = new GetJournalEntryHandler(_repository);

        Assert.Equal("Privato", (await handler.HandleAsync(TestUsers.A, entry.Id, CancellationToken.None))!.Content);
        Assert.Null(await handler.HandleAsync(TestUsers.B, entry.Id, CancellationToken.None));
        Assert.Null(await handler.HandleAsync(TestUsers.A, Guid.CreateVersion7(), CancellationToken.None));
    }

    // ---- Update ----

    [Fact]
    public async Task Update_ReplacesFields_AndStampsUpdatedAt()
    {
        var entry = await EntryAsync(TestUsers.A, Day, "Prima");
        _clock.Advance(TimeSpan.FromHours(2));

        var result = await UpdateAsync(TestUsers.A, entry.Id, Day.AddDays(-3), "Dopo", "Titolo");

        Assert.Equal(JournalResultStatus.Ok, result.Status);
        var stored = Assert.Single(_repository.Entries);
        Assert.Equal((Day.AddDays(-3), "Titolo", "Dopo"), (stored.OccurredAtUtc, stored.Title, stored.Content));
        Assert.Equal((Now, Now.AddHours(2)), (stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal(stored.UpdatedAtUtc, result.Entry!.UpdatedAtUtc);
    }

    [Fact]
    public async Task Update_Invalid_ChangesNothing()
    {
        var entry = await EntryAsync(TestUsers.A, Day, "Prima");
        _clock.Advance(TimeSpan.FromHours(2));

        var result = await UpdateAsync(TestUsers.A, entry.Id, Day, "  ");

        Assert.Equal((JournalResultStatus.Invalid, "content"), (result.Status, result.Field));
        var stored = Assert.Single(_repository.Entries);
        Assert.Equal(("Prima", Now), (stored.Content, stored.UpdatedAtUtc));
    }

    [Fact]
    public async Task Update_MissingOrOtherUsersEntry_IsNotFound_AndUntouched()
    {
        var entry = await EntryAsync(TestUsers.A, Day, "Privato");

        Assert.Equal(JournalResultStatus.NotFound, (await UpdateAsync(TestUsers.B, entry.Id, Day, "Rubato")).Status);
        Assert.Equal(JournalResultStatus.NotFound, (await UpdateAsync(TestUsers.A, Guid.CreateVersion7(), Day, "Nuovo")).Status);
        Assert.Equal("Privato", Assert.Single(_repository.Entries).Content);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_IsAHardDelete_ForTheOwnerOnly()
    {
        var entry = await EntryAsync(TestUsers.A, Day, "Privato");
        var handler = new DeleteJournalEntryHandler(_repository);

        Assert.Equal(JournalResultStatus.NotFound, await handler.HandleAsync(TestUsers.B, entry.Id, CancellationToken.None));
        Assert.Single(_repository.Entries);

        Assert.Equal(JournalResultStatus.Ok, await handler.HandleAsync(TestUsers.A, entry.Id, CancellationToken.None));
        Assert.Empty(_repository.Entries);
        Assert.Equal(JournalResultStatus.NotFound, await handler.HandleAsync(TestUsers.A, entry.Id, CancellationToken.None));
    }

    // ---- Timeline ----

    [Fact]
    public async Task Timeline_IsNewestOccurredFirst_ThenNewestCreated_AndOwnerOnly()
    {
        var old = await EntryAsync(TestUsers.A, Day.AddDays(-5), "old");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var sameDayFirst = await EntryAsync(TestUsers.A, Day, "same-1");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var sameDaySecond = await EntryAsync(TestUsers.A, Day, "same-2");
        _clock.Advance(TimeSpan.FromMinutes(1));
        // Written last, but about an earlier day: occurred-at decides.
        var backdated = await EntryAsync(TestUsers.A, Day.AddDays(-1), "backdated");
        await EntryAsync(TestUsers.B, Day.AddDays(1), "other user");

        var page = await PageAsync(TestUsers.A);

        Assert.Equal([sameDaySecond.Id, sameDayFirst.Id, backdated.Id, old.Id], page.Items.Select(entry => entry.Id));
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task Timeline_TiesOnBothTimestamps_AreOrderedByIdDescending()
    {
        var first = await EntryAsync(TestUsers.A, Day, "a");
        var second = await EntryAsync(TestUsers.A, Day, "b");
        var expected = new[] { first.Id, second.Id }.OrderDescending().ToList();

        Assert.Equal(expected, (await PageAsync(TestUsers.A)).Items.Select(entry => entry.Id));
    }

    [Fact]
    public async Task Pagination_WalksTheWholeTimeline_WithoutGapsOrRepeats()
    {
        var expected = new List<Guid>();

        for (var i = 0; i < 7; i++)
        {
            // Pairs share an occurred-at time, so the created-at and id tie-breaks are exercised.
            expected.Add((await EntryAsync(TestUsers.A, Day.AddDays(-(i / 2)), $"entry {i}")).Id);
            _clock.Advance(i % 3 == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(1));
        }

        var all = (await PageAsync(TestUsers.A, size: 50)).Items.Select(entry => entry.Id).ToList();
        Assert.Equal(7, all.Count);
        Assert.Equal(expected.Order(), all.Order());

        var walked = new List<Guid>();
        JournalCursor? cursor = null;
        var pages = 0;

        do
        {
            var page = await PageAsync(TestUsers.A, cursor, size: 3);
            walked.AddRange(page.Items.Select(entry => entry.Id));
            cursor = page.Next;
            pages++;

            // A page is full unless it is the last one.
            Assert.True(page.Items.Count == 3 || cursor is null);
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(all, walked);
    }

    [Fact]
    public async Task Pagination_ExactlyFullLastPage_HasNoNextCursor()
    {
        await EntryAsync(TestUsers.A, Day, "a");
        await EntryAsync(TestUsers.A, Day.AddDays(-1), "b");

        var first = await PageAsync(TestUsers.A, size: 2);

        Assert.Equal(2, first.Items.Count);
        Assert.Null(first.Next);
    }

    [Fact]
    public async Task Pagination_EntriesAddedWhilePaging_NeverShiftLaterPages()
    {
        var a = await EntryAsync(TestUsers.A, Day, "a");
        var b = await EntryAsync(TestUsers.A, Day.AddDays(-1), "b");
        var c = await EntryAsync(TestUsers.A, Day.AddDays(-2), "c");

        var first = await PageAsync(TestUsers.A, size: 2);
        Assert.Equal([a.Id, b.Id], first.Items.Select(entry => entry.Id));

        await EntryAsync(TestUsers.A, Day.AddDays(1), "newer");

        var second = await PageAsync(TestUsers.A, first.Next, size: 2);
        Assert.Equal([c.Id], second.Items.Select(entry => entry.Id));
        Assert.Null(second.Next);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(GetJournalEntriesHandler.MaxPageSize + 1)]
    public async Task Pagination_PageSizeOutOfRange_Throws(int size)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PageAsync(TestUsers.A, size: size));
    }
}
