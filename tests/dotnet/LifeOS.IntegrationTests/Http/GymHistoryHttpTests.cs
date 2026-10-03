using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Gym.Exercises;
using LifeOS.Contracts.Gym.History;
using LifeOS.Contracts.Gym.Programs;
using LifeOS.Contracts.Gym.Sessions;

namespace LifeOS.IntegrationTests.Http;

// Workout history and previous performance through the real pipeline: completed workouts only, keyset
// paging, read-only detail, previous values for the active workout, ownership and authentication.
public class GymHistoryHttpTests
{
    private const string Programs = "/api/gym/programs";
    private const string Sessions = "/api/gym/sessions";
    private const string History = "/api/gym/history";

    [Fact]
    public async Task History_ListsCompletedWorkoutsNewestFirst_AndPages()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var workout = await SingleWorkoutAsync(client, sets: 3);

        Assert.Empty((await client.GetFromJsonAsync<WorkoutHistoryPageResponse>(History))!.Items);

        var completed = new List<WorkoutSessionResponse>();
        for (var index = 0; index < 3; index++)
        {
            var session = await StartAsync(client, workout);
            await RecordAsync(client, session.Id, session.Blocks[0].Exercises[0].Sets[0].Id, 8, 80m);
            factory.Clock.Advance(TimeSpan.FromMinutes(30));
            completed.Add(await FinishAsync(client, session.Id));
            factory.Clock.Advance(TimeSpan.FromDays(1));
        }

        // In progress: not history.
        var current = await StartAsync(client, workout);

        var first = (await client.GetFromJsonAsync<WorkoutHistoryPageResponse>($"{History}?limit=2"))!;
        Assert.Equal([completed[2].Id, completed[1].Id], first.Items.Select(item => item.Id));
        Assert.NotNull(first.NextCursor);
        var item = first.Items[0];
        Assert.Equal(("Program", "Push", 1, 3, 1), (item.ProgramName, item.WorkoutName, item.CompletedSetCount, item.PrescribedSetCount, item.ExerciseCount));
        Assert.Equal((completed[2].StartedAtUtc, completed[2].CompletedAtUtc!.Value), (item.StartedAtUtc, item.CompletedAtUtc));

        var second = (await client.GetFromJsonAsync<WorkoutHistoryPageResponse>($"{History}?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}"))!;
        Assert.Equal([completed[0].Id], second.Items.Select(item => item.Id));
        Assert.Null(second.NextCursor);

        var all = (await client.GetFromJsonAsync<WorkoutHistoryPageResponse>(History))!;
        Assert.DoesNotContain(current.Id, all.Items.Select(item => item.Id));
        Assert.Equal(3, all.Items.Count);
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=51", "limit")]
    [InlineData("cursor=nonsense", "cursor")]
    [InlineData("cursor=123_not-a-guid", "cursor")]
    public async Task History_InvalidPagingParameters_Are400(string query, string field)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.GetAsync($"{History}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HistoryDetail_IsTheCompletedSnapshot_AndSurvivesDeletingTheProgram()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var workout = await SingleWorkoutAsync(client, sets: 3);
        var session = await StartAsync(client, workout);

        // Not history while in progress.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{History}/{session.Id}")).StatusCode);

        await RecordAsync(client, session.Id, session.Blocks[0].Exercises[0].Sets[0].Id, 8, 82.5m);
        await RecordAsync(client, session.Id, session.Blocks[0].Exercises[0].Sets[1].Id, 6, null);
        factory.Clock.Advance(TimeSpan.FromMinutes(40));
        await FinishAsync(client, session.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Programs}/{workout.ProgramId}")).StatusCode);

        var detail = (await client.GetFromJsonAsync<WorkoutSessionResponse>($"{History}/{session.Id}"))!;

        Assert.Equal(("Completed", "Program", "Push", 2, 3), (detail.Status, detail.ProgramName, detail.WorkoutName, detail.CompletedSetCount, detail.PrescribedSetCount));
        Assert.True(detail.CompletedAtUtc >= detail.StartedAtUtc.AddMinutes(40));
        Assert.Equal(
            [(8, 8, (int?)8, (decimal?)82.5m), (8, 8, 6, null), (8, 8, null, null)],
            detail.Blocks[0].Exercises[0].Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps, set.ActualReps, set.WeightKg)));
        Assert.Null(detail.Rest);
        Assert.Single((await client.GetFromJsonAsync<WorkoutHistoryPageResponse>(History))!.Items);
    }

    [Fact]
    public async Task PreviousPerformance_IsTheLastCompletedWorkoutsValues_ForTheCurrentSessionsExercises()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var workout = await SingleWorkoutAsync(client, sets: 3);

        var first = await StartAsync(client, workout);
        Assert.Empty((await PreviousAsync(client, first.Id)).Exercises);
        var sets = first.Blocks[0].Exercises[0].Sets;
        await RecordAsync(client, first.Id, sets[0].Id, 8, 82.5m);
        await RecordAsync(client, first.Id, sets[1].Id, 7, 82.5m);
        // The current workout's own values are never "previous".
        Assert.Empty((await PreviousAsync(client, first.Id)).Exercises);
        factory.Clock.Advance(TimeSpan.FromMinutes(30));
        var finished = await FinishAsync(client, first.Id);
        factory.Clock.Advance(TimeSpan.FromDays(2));

        var second = await StartAsync(client, workout);
        var previous = await PreviousAsync(client, second.Id);

        Assert.Equal(second.Id, previous.SessionId);
        var exercise = Assert.Single(previous.Exercises);
        Assert.Equal(
            (first.Blocks[0].Exercises[0].ExerciseId, first.Id, "Push", finished.CompletedAtUtc!.Value),
            (exercise.ExerciseId, exercise.SessionId, exercise.WorkoutName, exercise.CompletedAtUtc));
        Assert.Equal([new PreviousSetResponse(1, 1, 8, 82.5m), new PreviousSetResponse(1, 2, 7, 82.5m)], exercise.Sets);
    }

    // ---- Exercise history (UI-001) ----

    [Fact]
    public async Task ExerciseHistory_IsTheExercisesCompletedWorkouts_NewestFirst_PagedByCursor_WithoutTheCurrentOne()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var workout = await SingleWorkoutAsync(client, sets: 3);

        var completed = new List<WorkoutSessionResponse>();
        for (var index = 0; index < 3; index++)
        {
            var session = await StartAsync(client, workout);
            var sets = session.Blocks[0].Exercises[0].Sets;
            await RecordAsync(client, session.Id, sets[0].Id, 8, 80m + index);
            await RecordAsync(client, session.Id, sets[1].Id, 6, null);
            factory.Clock.Advance(TimeSpan.FromMinutes(30));
            completed.Add(await FinishAsync(client, session.Id));
            factory.Clock.Advance(TimeSpan.FromDays(1));
        }

        var current = await StartAsync(client, workout);
        var exerciseId = current.Blocks[0].Exercises[0].ExerciseId;
        await RecordAsync(client, current.Id, current.Blocks[0].Exercises[0].Sets[0].Id, 8, 90m);

        var first = await ExerciseHistoryAsync(client, current.Id, exerciseId, "?limit=2");
        Assert.Equal(exerciseId, first.ExerciseId);
        Assert.Equal([completed[2].Id, completed[1].Id], first.Items.Select(item => item.SessionId));
        Assert.NotNull(first.NextCursor);
        var latest = first.Items[0];
        Assert.Equal(("Program", "Push", completed[2].CompletedAtUtc!.Value), (latest.ProgramName, latest.WorkoutName, latest.CompletedAtUtc));
        Assert.Equal([new PreviousSetResponse(1, 1, 8, 82m), new PreviousSetResponse(1, 2, 6, null)], latest.Sets);

        var second = await ExerciseHistoryAsync(client, current.Id, exerciseId, $"?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal([completed[0].Id], second.Items.Select(item => item.SessionId));
        Assert.Null(second.NextCursor);

        // Default page size: 5, so all three fit on one page.
        var all = await ExerciseHistoryAsync(client, current.Id, exerciseId, "");
        Assert.Equal(3, all.Items.Count);
        Assert.DoesNotContain(current.Id, all.Items.Select(item => item.SessionId));
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=21", "limit")]
    [InlineData("cursor=nonsense", "cursor")]
    public async Task ExerciseHistory_InvalidPagingParameters_Are400(string query, string field)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var current = await StartAsync(client, await SingleWorkoutAsync(client, sets: 1));

        var response = await client.GetAsync($"{Sessions}/{current.Id}/exercises/{current.Blocks[0].Exercises[0].ExerciseId}/history?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExerciseHistory_IsOwnerScoped_AndLimitedToTheSessionsExercises()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var push = await SingleWorkoutAsync(userA, sets: 1);
        var pull = await SingleWorkoutAsync(userA, sets: 1);
        var completedPull = await StartAsync(userA, pull);
        await RecordAsync(userA, completedPull.Id, completedPull.Blocks[0].Exercises[0].Sets[0].Id, 10, 60m);
        await FinishAsync(userA, completedPull.Id);
        var current = await StartAsync(userA, push);
        var bench = current.Blocks[0].Exercises[0].ExerciseId;
        var pullExercise = completedPull.Blocks[0].Exercises[0].ExerciseId;

        // Another user's session, a missing session, and an exercise this session does not contain.
        Assert.Equal(HttpStatusCode.NotFound, (await userB.GetAsync($"{Sessions}/{current.Id}/exercises/{bench}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await userA.GetAsync($"{Sessions}/{Guid.NewGuid()}/exercises/{bench}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await userA.GetAsync($"{Sessions}/{current.Id}/exercises/{pullExercise}/history")).StatusCode);

        // User B's own workout with the same route shape sees none of A's history.
        var othersCurrent = await StartAsync(userB, await SingleWorkoutAsync(userB, sets: 1));
        Assert.Empty((await ExerciseHistoryAsync(userB, othersCurrent.Id, othersCurrent.Blocks[0].Exercises[0].ExerciseId, "")).Items);
        Assert.Empty((await ExerciseHistoryAsync(userA, current.Id, bench, "")).Items);
    }

    [Fact]
    public async Task ExerciseHistoryFailure_DoesNotBreakTheActiveWorkout()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var current = await StartAsync(client, await SingleWorkoutAsync(client, sets: 2));
        var exercise = current.Blocks[0].Exercises[0];
        factory.WorkoutSessions.FailExerciseHistory = true;

        HttpStatusCode? historyStatus = null;
        try
        {
            historyStatus = (await client.GetAsync($"{Sessions}/{current.Id}/exercises/{exercise.ExerciseId}/history")).StatusCode;
        }
        catch (Exception)
        {
            // The test host may surface the server's exception to the caller instead of a 500.
        }

        Assert.NotEqual(HttpStatusCode.OK, historyStatus);

        // The workout itself goes on: read, previous performance, record, finish.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Sessions}/{current.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Sessions}/{current.Id}/previous-performance")).StatusCode);
        await RecordAsync(client, current.Id, exercise.Sets[0].Id, 8, 80m);
        Assert.Equal("Completed", (await FinishAsync(client, current.Id)).Status);
    }

    // ---- Ownership / authentication ----

    [Fact]
    public async Task AnotherUsersHistory_IsInvisible()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var workout = await SingleWorkoutAsync(userA, sets: 1);
        var completed = await StartAsync(userA, workout);
        await RecordAsync(userA, completed.Id, completed.Blocks[0].Exercises[0].Sets[0].Id, 8, 80m);
        await FinishAsync(userA, completed.Id);
        var current = await StartAsync(userA, workout);

        Assert.Empty((await userB.GetFromJsonAsync<WorkoutHistoryPageResponse>(History))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await userB.GetAsync($"{History}/{completed.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await userB.GetAsync($"{Sessions}/{current.Id}/previous-performance")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await userA.GetAsync($"{Sessions}/{Guid.NewGuid()}/previous-performance")).StatusCode);
        Assert.Single((await PreviousAsync(userA, current.Id)).Exercises);
    }

    [Fact]
    public async Task HistoryEndpoints_RequireAnAccessToken()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await client.GetAsync(History),
            await client.GetAsync($"{History}/{id}"),
            await client.GetAsync($"{Sessions}/{id}/previous-performance"),
            await client.GetAsync($"{Sessions}/{id}/exercises/{Guid.NewGuid()}/history")
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task History_HasNoMutatingRoutes()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await client.DeleteAsync($"{History}/{id}"),
            await client.PutAsJsonAsync($"{History}/{id}", new { }),
            await client.PostAsync(History, null)
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode));
    }

    // ---- Helpers ----

    // A "Push" workout with one Single bench-press block of sets × 8 reps.
    private static async Task<(Guid ProgramId, Guid WorkoutId)> SingleWorkoutAsync(HttpClient client, int sets)
    {
        var program = await AddWorkoutAsync(client, (await CreateProgramAsync(client)).Id, "Push");
        var bench = await CreateExerciseAsync(client, $"Bench press {Guid.NewGuid():N}");
        await AddBlockAsync(client, program.Id, program.Workouts[0].Id, new CreateWorkoutBlockRequest("Single", 90, [Exercise(bench.Id, sets, 8)]));

        return (program.Id, program.Workouts[0].Id);
    }

    private static async Task<ExerciseHistoryPageResponse> ExerciseHistoryAsync(HttpClient client, Guid sessionId, Guid exerciseId, string query)
    {
        var response = await client.GetAsync($"{Sessions}/{sessionId}/exercises/{exerciseId}/history{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ExerciseHistoryPageResponse>())!;
    }

    private static async Task<PreviousPerformanceResponse> PreviousAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.GetAsync($"{Sessions}/{sessionId}/previous-performance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PreviousPerformanceResponse>())!;
    }

    private static async Task<WorkoutSessionResponse> StartAsync(HttpClient client, (Guid ProgramId, Guid WorkoutId) workout)
    {
        var response = await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(workout.ProgramId, workout.WorkoutId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkoutSessionResponse>())!;
    }

    private static async Task<WorkoutSessionResponse> RecordAsync(HttpClient client, Guid sessionId, Guid setId, int reps, decimal? weightKg)
    {
        var response = await client.PutAsJsonAsync($"{Sessions}/{sessionId}/sets/{setId}", new RecordWorkoutSetRequest(reps, weightKg));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkoutSessionResponse>())!;
    }

    private static async Task<WorkoutSessionResponse> FinishAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.PostAsync($"{Sessions}/{sessionId}/finish", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkoutSessionResponse>())!;
    }

    private static WorkoutBlockExerciseRequest Exercise(Guid exerciseId, int sets, int reps) =>
        new(exerciseId, null, Enumerable.Repeat(new WorkoutSetRequest(reps, reps), sets).ToList());

    private static async Task<WorkoutProgramResponse> CreateProgramAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(Programs, new CreateWorkoutProgramRequest("Program"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!;
    }

    private static async Task<WorkoutProgramResponse> AddWorkoutAsync(HttpClient client, Guid programId, string name)
    {
        var response = await client.PostAsJsonAsync($"{Programs}/{programId}/workouts", new CreateWorkoutRequest(name));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!;
    }

    private static async Task AddBlockAsync(HttpClient client, Guid programId, Guid workoutId, CreateWorkoutBlockRequest request)
    {
        var response = await client.PostAsJsonAsync($"{Programs}/{programId}/workouts/{workoutId}/blocks", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<ExerciseResponse> CreateExerciseAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/gym/exercises", new CreateExerciseRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ExerciseResponse>())!;
    }

    private static async Task<HttpClient> SignInAsync(LifeOSApiFactory factory, string subject)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest(subject, null, null));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        return client;
    }
}
