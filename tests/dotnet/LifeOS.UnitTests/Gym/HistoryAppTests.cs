using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using LifeOS.App.Services.Gym;
using LifeOS.Contracts.Gym.History;

namespace LifeOS.UnitTests.Gym;

// Workout history in the Gym client: its presentation helper and API paging (plain .NET), and the
// read-only pages, which the net10.0 test project cannot render. Those checks read the page sources.
public class HistoryAppTests
{
    // UTC+2, no daylight saving: 22:30 UTC is already the next local day.
    private static readonly TimeZoneInfo Local = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");
    private static readonly DateTime Today = new(2026, 10, 2);

    [Fact]
    public void Day_IsTheLocalDay()
    {
        Assert.Equal("Today", HistoryDisplay.Day(Utc(2026, 10, 1, 22, 30), Today, Local));
        Assert.Equal("Yesterday", HistoryDisplay.Day(Utc(2026, 10, 1, 8, 0), Today, Local));
        Assert.Equal("28 Sep", HistoryDisplay.Day(Utc(2026, 9, 28, 8, 0), Today, Local));
        Assert.Equal("28 Sep 2025", HistoryDisplay.Day(Utc(2025, 9, 28, 8, 0), Today, Local));
    }

    [Fact]
    public void When_ShowsLocalDayAndTime_InTheDeviceCulture()
    {
        Assert.Equal("Today 00:30", HistoryDisplay.When(Utc(2026, 10, 1, 22, 30), Today, Local, Culture));
        Assert.Equal("28 Sep · 20:05", HistoryDisplay.When(Utc(2026, 9, 28, 18, 5), Today, Local, Culture));
        Assert.Equal("Monday 28 September 2026", HistoryDisplay.LongDate(Utc(2026, 9, 28, 18, 5), Local));
    }

    [Theory]
    [InlineData(0, "< 1 min")]
    [InlineData(59, "< 1 min")]
    [InlineData(60, "1 min")]
    [InlineData(52 * 60 + 40, "52 min")]
    [InlineData(65 * 60, "1 h 05 min")]
    [InlineData(2 * 3600 + 30 * 60, "2 h 30 min")]
    public void Duration_IsWholeMinutes(int seconds, string expected)
    {
        var started = Utc(2026, 10, 1, 18, 0);

        Assert.Equal(expected, HistoryDisplay.Duration(started, started.AddSeconds(seconds)));
    }

    [Fact]
    public void Summary_IsWhenDurationAndSets()
    {
        var item = new WorkoutHistoryItemResponse(Guid.NewGuid(), "Program", "Push", Utc(2026, 10, 2, 7, 0), Utc(2026, 10, 2, 7, 52), 14, 15, 5);

        Assert.Equal("Today 09:52 · 52 min · 14 / 15 sets", HistoryDisplay.Summary(item, Today, Local, Culture));
    }

    [Fact]
    public void Set_ShowsWeightAndReps_OrBodyweight()
    {
        Assert.Equal("82.5 kg × 8", HistoryDisplay.Set(new PreviousSetResponse(1, 1, 8, 82.5m)));
        Assert.Equal("100 kg × 5", HistoryDisplay.Set(new PreviousSetResponse(1, 2, 5, 100m)));
        Assert.Equal("BW × 12", HistoryDisplay.Set(new PreviousSetResponse(1, 3, 12, null)));
    }

    [Fact]
    public void Occurrences_GroupSetsByBlock_InExecutionOrder_WithoutReAligning()
    {
        var previous = new PreviousExercisePerformanceResponse(Guid.NewGuid(), Guid.NewGuid(), "Push", Utc(2026, 9, 28, 18, 0),
        [
            new PreviousSetResponse(3, 1, 10, 70m),
            new PreviousSetResponse(1, 3, 6, 100m),
            new PreviousSetResponse(1, 1, 8, 100m)
        ]);

        var groups = HistoryDisplay.Occurrences(previous);

        Assert.Equal([[(1, 1), (1, 3)], [(3, 1)]], groups.Select(group => group.Select(set => (set.BlockPosition, set.Position)).ToList()).ToList());
    }

    [Fact]
    public void ByExercise_IsEmptyWithoutPreviousPerformance()
    {
        Assert.Empty(HistoryDisplay.ByExercise(null));
        var exerciseId = Guid.NewGuid();
        var previous = new PreviousPerformanceResponse(Guid.NewGuid(), [new(exerciseId, Guid.NewGuid(), "Push", Utc(2026, 9, 28, 18, 0), [])]);
        Assert.Equal([exerciseId], HistoryDisplay.ByExercise(previous).Keys);
    }

    // ---- API client ----

    [Fact]
    public async Task GetHistory_FirstPageHasNoCursor_ThenPassesTheReturnedCursor()
    {
        var requested = new List<string>();
        var handler = new StubHandler(request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);
            var page = requested.Count == 1
                ? new WorkoutHistoryPageResponse([Item("A")], "638950000000000000_0193a")
                : new WorkoutHistoryPageResponse([Item("B")], null);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page) };
        });
        var client = new WorkoutSessionsApiClient(new HttpClient(handler) { BaseAddress = new("http://test/") });

        var first = await client.GetHistoryAsync(null);
        var second = await client.GetHistoryAsync(first.Value!.NextCursor);

        Assert.Equal(["/api/gym/history", "/api/gym/history?cursor=638950000000000000_0193a"], requested);
        Assert.Equal(["A", "B"], first.Value.Items.Concat(second.Value!.Items).Select(item => item.WorkoutName));
        Assert.Null(second.Value.NextCursor);
    }

    // ---- Pages ----

    [Fact]
    public void GymHub_OffersHistory_BetweenTrainAndPrograms()
    {
        var hub = PageSource("GymHub.razor");

        var train = hub.IndexOf("href=\"gym/train\"", StringComparison.Ordinal);
        var history = hub.IndexOf("href=\"gym/history\"", StringComparison.Ordinal);
        var programs = hub.IndexOf("href=\"gym/programs\"", StringComparison.Ordinal);
        Assert.True(train >= 0 && train < history && history < programs);
    }

    [Fact]
    public void History_PagesWithLoadMore_AndOpensReadOnlyDetail()
    {
        var history = PageSource("History.razor");

        Assert.Contains("@page \"/gym/history\"", history);
        Assert.Contains("SessionsApi.GetHistoryAsync(nextCursor)", history);
        Assert.Contains("items.AddRange(result.Value!.Items)", history);
        Assert.Contains("Load more", history);
        Assert.Contains("No completed workouts yet.", history);
        Assert.Contains("gym/history/{item.Id}", history);
    }

    [Fact]
    public void HistoryDetail_IsReadOnly()
    {
        var detail = PageSource("HistoryDetail.razor");

        Assert.Contains("@page \"/gym/history/{SessionId:guid}\"", detail);
        Assert.Contains("SessionsApi.GetHistoryDetailAsync(SessionId)", detail);
        Assert.Contains("Not done", detail);
        foreach (var mutation in new[] { "RecordSetAsync", "FinishAsync", "DiscardAsync", "StartAsync", "<input", "@bind" })
        {
            Assert.DoesNotContain(mutation, detail);
        }
    }

    [Fact]
    public void ActiveWorkout_ShowsPreviousPerformance_FromOneRead()
    {
        var active = PageSource("ActiveWorkout.razor");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(active, "GetPreviousPerformanceAsync"));
        Assert.Contains("previous.TryGetValue(exercise.ExerciseId, out var last)", active);
    }

    private static WorkoutHistoryItemResponse Item(string name) =>
        new(Guid.NewGuid(), "Program", name, Utc(2026, 10, 1, 18, 0), Utc(2026, 10, 1, 19, 0), 3, 3, 1);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static string PageSource(string fileName, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components", "Pages", "Gym", fileName)));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
