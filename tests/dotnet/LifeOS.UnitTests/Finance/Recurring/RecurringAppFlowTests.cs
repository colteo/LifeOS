using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Finance.Recurring;

namespace LifeOS.UnitTests.Finance.Recurring;

public class RecurringAppFlowTests
{
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
    private static RecurringOccurrenceResponse Occurrence(string status = "Due") => new(Guid.NewGuid(), "Car", "Expense",
        Guid.NewGuid(), Guid.NewGuid(), "EUR", 250, "note", 2026, 9, new(2026, 9, 1), status, null);

    [Fact]
    public void SeptemberDefault_ParsesUsingSeptemberOffset_IndependentOfTodayAndCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            var flow = new RecurringOccurrenceFlow(Occurrence()); flow.Review();
            Assert.Equal("2026-09-01T12:00", flow.LocalDateTime);
            // The original page's exact default also parses; this alone does not reproduce
            // the physical failure. Do not attribute it to Domain scheduling or the default.
            Assert.True(DateTime.TryParseExact(flow.LocalDateTime, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            Assert.True(flow.TryConfirmation(Rome, out var request));
            Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), request!.OccurredAtUtc);
            flow.LocalDateTime = "2026-12-01T12:00";
            Assert.True(flow.TryConfirmation(Rome, out request));
            Assert.Equal(11, request!.OccurredAtUtc.Hour); // Historical date's offset, not current October's +02.
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("2026-09-01T12:00")] [InlineData("2026-09-01T12:00:00")]
    [InlineData("2026-09-01T12:00:00.123")]
    public void HtmlLocalFormats_AreAccepted(string text)
    {
        var flow = new RecurringOccurrenceFlow(Occurrence()); flow.Review(); flow.LocalDateTime = text;
        Assert.True(flow.TryConfirmation(Rome, out var request)); Assert.Equal(9, request!.OccurredAtUtc.Month);
    }

    [Theory]
    [InlineData("invalid", "250", "valid local")]
    [InlineData("2026-09-01T12:00Z", "250", "valid local")]
    [InlineData("2026-09-01T12:00", "0", "positive actual")]
    [InlineData("2026-03-29T02:30", "250", "does not exist")]
    [InlineData("2026-10-25T02:30", "250", "ambiguous")]
    public void ValidationError_IsInActiveFlow_AndSubmissionRemainsAvailable(string date, string amount, string error)
    {
        var flow = new RecurringOccurrenceFlow(Occurrence()); flow.Review(); flow.LocalDateTime = date; flow.Amount = amount;
        Assert.False(flow.TryConfirmation(Rome, out _)); Assert.Contains(error, flow.Error);
        Assert.True(flow.Reviewing); Assert.False(flow.Busy); Assert.True(flow.CanConfirm);
        flow.LocalDateTime = "2026-09-01T12:00"; flow.Amount = "250";
        Assert.True(flow.TryConfirmation(Rome, out _)); Assert.Null(flow.Error);
    }

    [Theory]
    [InlineData("Due", true, true)] [InlineData("Projected", false, true)]
    [InlineData("Confirmed", false, false)] [InlineData("Skipped", false, false)]
    public async Task StatusActions_AreSharedAcrossBothSurfaces(string status, bool confirm, bool skip)
    {
        var flow = new RecurringOccurrenceFlow(Occurrence(status));
        Assert.Equal(confirm, flow.CanConfirm); Assert.Equal(skip, flow.CanSkip);
        flow.Review(); Assert.Equal(confirm, flow.Reviewing);
        if (!confirm)
        {
            var handler = new StubHandler(_ => throw new InvalidOperationException("Must not send confirmation"));
            Assert.False(await flow.ConfirmAsync(Client(handler), Rome)); Assert.Equal(0, handler.Calls);
        }
    }

    [Theory]
    [InlineData("validation")] [InlineData("transport")] [InlineData("unreadable")] [InlineData("unexpected")]
    public async Task FailedSubmission_ReportsLocalError_ResetsBusy_AndCanRetry(string failure)
    {
        var id = Guid.NewGuid();
        var handler = new StubHandler(call => call == 1 ? failure switch
        {
            "validation" => new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { errors = new { amount = new[] { "Amount exceeds limit." } } }) },
            "transport" => throw new HttpRequestException("offline"),
            "unreadable" => new(HttpStatusCode.OK) { Content = new StringContent("not-json") },
            _ => throw new InvalidOperationException("unexpected client failure")
        } : new(HttpStatusCode.OK) { Content = JsonContent.Create(new RecurringActionResponse(id)) });
        var flow = new RecurringOccurrenceFlow(Occurrence()); flow.Review(); var api = Client(handler);
        Assert.False(await flow.ConfirmAsync(api, Rome));
        Assert.False(flow.Busy); Assert.True(flow.Reviewing); Assert.NotEmpty(flow.Error!); Assert.False(flow.Completed);
        Assert.True(await flow.ConfirmAsync(api, Rome)); Assert.False(flow.Busy); Assert.Null(flow.Error);
        Assert.True(flow.Completed); Assert.False(flow.CanConfirm); Assert.False(flow.CanSkip);
        Assert.False(await flow.ConfirmAsync(api, Rome)); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task WhileSubmitting_DoubleTapCannotSendAgain()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new AsyncHandler(() => completion.Task); var api = new RecurringApiClient(new(handler) { BaseAddress = new("http://test/") });
        var flow = new RecurringOccurrenceFlow(Occurrence()); flow.Review(); var first = flow.ConfirmAsync(api, Rome);
        Assert.True(flow.Busy); Assert.False(await flow.ConfirmAsync(api, Rome)); Assert.False(await flow.SkipAsync(api));
        completion.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new RecurringActionResponse(Guid.NewGuid())) });
        Assert.True(await first); Assert.False(flow.Busy); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Client_UsesLogicalSeptemberMonth_ExplicitUtcBody_AndCurrentLocalOffset()
    {
        var o = Occurrence(); string? uri = null; ConfirmRecurringRequest? body = null;
        var handler = new AsyncHandler(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RecurringActionResponse(Guid.NewGuid())) }));
        handler.Inspect = async request => { uri = request.RequestUri!.ToString(); body = await request.Content!.ReadFromJsonAsync<ConfirmRecurringRequest>(); };
        var flow = new RecurringOccurrenceFlow(o); flow.Review();
        Assert.True(await flow.ConfirmAsync(new(new(handler) { BaseAddress = new("http://test/") }), Rome));
        var offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;
        Assert.Contains($"/{o.RuleId}/2026/9/confirm?utcOffsetMinutes={offset}", uri);
        Assert.Equal(TimeSpan.Zero, body!.OccurredAtUtc.Offset); Assert.Equal(10, body.OccurredAtUtc.Hour);
    }

    [Fact]
    public async Task MonthChanges_ReloadActualAndPlanning_WithSameSelectedMonthBoundaries()
    {
        var paths = new List<Uri>();
        var handler = new AsyncHandler(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") }));
        handler.Inspect = request => { paths.Add(request.RequestUri!); return Task.CompletedTask; };
        // Recurring needs its own response shape, while both clients share the capture.
        var planningHandler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(new RecurringResponse([], [])) });
        planningHandler.Inspect = request => paths.Add(request.RequestUri!);
        var actual = new TransactionsApiClient(new(handler) { BaseAddress = new("http://test/") }); var plans = Client(planningHandler);
        foreach (var month in new[] { 9, 10 })
            await TransactionMonthLoader.LoadAsync(new(2026, month, 15), Rome, actual, plans);
        Assert.Equal(4, paths.Count);
        Assert.Contains(paths, p => p.Query.Contains("fromMonth=9&toYear=2026&toMonth=9"));
        Assert.Contains(paths, p => p.Query.Contains("fromMonth=10&toYear=2026&toMonth=10"));
        var actualQueries = paths.Where(p => p.AbsolutePath == "/api/transactions").Select(p => Uri.UnescapeDataString(p.Query)).ToList();
        Assert.Contains(actualQueries, q => q.Contains("fromUtc=2026-08-31T22:00:00"));
        Assert.Contains(actualQueries, q => q.Contains("fromUtc=2026-09-30T22:00:00") && q.Contains("toUtc=2026-10-31T23:00:00"));
    }

    private static RecurringApiClient Client(HttpMessageHandler handler) => new(new(handler) { BaseAddress = new("http://test/") });
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Action<HttpRequestMessage>? Inspect { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Inspect?.Invoke(request); return Task.FromResult(respond(++Calls)); }
    }
    private sealed class AsyncHandler(Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, Task>? Inspect { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; if (Inspect is not null) await Inspect(request); return await respond(); }
    }
}
