using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Gym.Exercises;
using LifeOS.Contracts.Gym.Programs;
using LifeOS.Contracts.Gym.Sessions;

namespace LifeOS.IntegrationTests.Http;

// Workout execution through the real pipeline (routing, JWT, binding, status mapping).
public class GymSessionHttpTests
{
    private const string Programs = "/api/gym/programs";
    private const string Sessions = "/api/gym/sessions";

    [Fact]
    public async Task StartRecordCorrectFinish_EndToEnd()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var (programId, workoutId) = await SingleWorkoutAsync(client, sets: 3, rest: 90);

        var started = await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(programId, workoutId));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var session = (await started.Content.ReadFromJsonAsync<WorkoutSessionResponse>())!;
        Assert.Equal(("InProgress", "Push", 0, 3), (session.Status, session.WorkoutName, session.CompletedSetCount, session.PrescribedSetCount));
        Assert.Equal($"{Sessions}/{session.Id}", started.Headers.Location!.OriginalString);
        var sets = session.Blocks[0].Exercises[0].Sets;
        Assert.Equal([(1, 8, 8), (2, 8, 8), (3, 8, 8)], sets.Select(set => (set.Position, set.TargetMinReps, set.TargetMaxReps)));

        var recorded = await RecordAsync(client, session.Id, sets[0].Id, 8, 82.5m);
        var first = recorded.Blocks[0].Exercises[0].Sets[0];
        Assert.Equal((8, (decimal?)82.5m), (first.ActualReps, first.WeightKg));
        Assert.NotNull(first.CompletedAtUtc);
        // The server's clock decides; the prescribed rest starts with the completion.
        Assert.Equal((first.CompletedAtUtc!.Value, first.CompletedAtUtc.Value.AddSeconds(90), 90), (recorded.Rest!.StartedAtUtc, recorded.Rest.EndsAtUtc, recorded.Rest.Seconds));
        Assert.True(recorded.ServerTimeUtc >= first.CompletedAtUtc);

        // Correct set 1: values change, the completion time does not.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var corrected = (await RecordAsync(client, session.Id, sets[0].Id, 7, null)).Blocks[0].Exercises[0].Sets[0];
        Assert.Equal((7, (decimal?)null, first.CompletedAtUtc), (corrected.ActualReps, corrected.WeightKg, corrected.CompletedAtUtc));

        await RecordAsync(client, session.Id, sets[1].Id, 8, 80m);

        // Stop early: one set never done.
        factory.Clock.Advance(TimeSpan.FromMinutes(20));
        var finish = await client.PostAsync($"{Sessions}/{session.Id}/finish", null);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        var finished = (await finish.Content.ReadFromJsonAsync<WorkoutSessionResponse>())!;
        Assert.Equal(("Completed", 2, 3), (finished.Status, finished.CompletedSetCount, finished.PrescribedSetCount));
        Assert.True(finished.CompletedAtUtc >= finished.StartedAtUtc.AddMinutes(21));
        Assert.Null(finished.Rest);

        // Reload.
        var reloaded = (await client.GetFromJsonAsync<WorkoutSessionResponse>($"{Sessions}/{session.Id}"))!;
        Assert.Equal((finished.Status, finished.CompletedAtUtc), (reloaded.Status, reloaded.CompletedAtUtc));
        Assert.Equal([(int?)7, 8, null], reloaded.Blocks[0].Exercises[0].Sets.Select(set => set.ActualReps));
    }

    [Fact]
    public async Task CompletedWorkout_CannotBeChangedOrDiscarded()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var session = await StartAsync(client, await SingleWorkoutAsync(client, sets: 2, rest: 90));
        var setId = session.Blocks[0].Exercises[0].Sets[0].Id;
        await RecordAsync(client, session.Id, setId, 8, 80m);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Sessions}/{session.Id}/finish", null)).StatusCode);

        var responses = new[]
        {
            await client.PutAsJsonAsync($"{Sessions}/{session.Id}/sets/{setId}", new RecordWorkoutSetRequest(10, 100m)),
            await client.PutAsJsonAsync($"{Sessions}/{session.Id}/sets/{session.Blocks[0].Exercises[0].Sets[1].Id}", new RecordWorkoutSetRequest(10, 100m)),
            await client.PostAsync($"{Sessions}/{session.Id}/finish", null),
            await client.DeleteAsync($"{Sessions}/{session.Id}")
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        Assert.Contains("already finished", await responses[0].Content.ReadAsStringAsync());
        var stored = (await client.GetFromJsonAsync<WorkoutSessionResponse>($"{Sessions}/{session.Id}"))!;
        Assert.Equal(("Completed", 1), (stored.Status, stored.CompletedSetCount));
        Assert.Equal((8, (decimal?)80m), (stored.Blocks[0].Exercises[0].Sets[0].ActualReps, stored.Blocks[0].Exercises[0].Sets[0].WeightKg));
    }

    [Fact]
    public async Task Superset_RestStartsAfterBOfEachRound()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await AddWorkoutAsync(client, (await CreateProgramAsync(client)).Id, "Pull");
        var row = await CreateExerciseAsync(client, "Row");
        var curl = await CreateExerciseAsync(client, "Curl");
        await AddBlockAsync(client, program.Id, program.Workouts[0].Id,
            new CreateWorkoutBlockRequest("Superset", 120, [Exercise(row.Id, 2, 10), Exercise(curl.Id, 2, 12)]));
        var session = await StartAsync(client, (program.Id, program.Workouts[0].Id));
        var block = Assert.Single(session.Blocks);
        Assert.Equal(("Superset", "Row", "Curl"), (block.Kind, block.Exercises[0].ExerciseName, block.Exercises[1].ExerciseName));
        var (a, b) = (block.Exercises[0].Sets, block.Exercises[1].Sets);

        var afterA1 = await RecordAsync(client, session.Id, a[0].Id, 10, 60m);
        Assert.Null(afterA1.Rest);

        var afterB1 = await RecordAsync(client, session.Id, b[0].Id, 12, 15m);
        Assert.Equal(120, afterB1.Rest!.Seconds);
        Assert.Equal(afterB1.Blocks[0].Exercises[1].Sets[0].CompletedAtUtc, afterB1.Rest.StartedAtUtc);

        var afterA2 = await RecordAsync(client, session.Id, a[1].Id, 10, 60m);
        Assert.Null(afterA2.Rest);

        // The last set of the workout: nothing remains to rest for.
        var afterB2 = await RecordAsync(client, session.Id, b[1].Id, 12, 15m);
        Assert.Null(afterB2.Rest);
        Assert.Equal(4, afterB2.CompletedSetCount);
    }

    [Fact]
    public async Task InProgressWorkout_IsResumable_AndBlocksAnotherStart()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var workout = await SingleWorkoutAsync(client, sets: 2, rest: 90);

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"{Sessions}/current")).StatusCode);

        var session = await StartAsync(client, workout);
        await RecordAsync(client, session.Id, session.Blocks[0].Exercises[0].Sets[0].Id, 8, 80m);

        var current = (await client.GetFromJsonAsync<WorkoutSessionResponse>($"{Sessions}/current"))!;
        Assert.Equal((session.Id, 1), (current.Id, current.CompletedSetCount));

        var again = await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(workout.ProgramId, workout.WorkoutId));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using var problem = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal(session.Id, problem.RootElement.GetProperty("sessionId").GetGuid());

        // Discard: the workout and what was recorded are gone; a new one can start.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Sessions}/{session.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Sessions}/{session.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"{Sessions}/current")).StatusCode);

        var next = await StartAsync(client, workout);
        Assert.NotEqual(session.Id, next.Id);
        Assert.Equal(0, next.CompletedSetCount);
    }

    [Fact]
    public async Task TemplateEditsAndDeletion_AfterStart_DoNotChangeTheWorkout()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var (programId, workoutId) = await SingleWorkoutAsync(client, sets: 3, rest: 90);
        var session = await StartAsync(client, (programId, workoutId));
        var program = (await client.GetFromJsonAsync<WorkoutProgramResponse>($"{Programs}/{programId}"))!;
        var block = program.Workouts[0].Blocks[0];

        // The template becomes 4 × 10 with 60 s rest, then the whole program is deleted.
        var update = await client.PutAsJsonAsync(
            $"{Programs}/{programId}/workouts/{workoutId}/blocks/{block.Id}",
            new UpdateWorkoutBlockRequest(60, [Exercise(block.Exercises[0].ExerciseId, 4, 10)]));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Programs}/{programId}")).StatusCode);

        var stored = (await client.GetFromJsonAsync<WorkoutSessionResponse>($"{Sessions}/{session.Id}"))!;
        Assert.Equal(("Push", 90), (stored.WorkoutName, stored.Blocks[0].RestSeconds));
        Assert.Equal([(8, 8), (8, 8), (8, 8)], stored.Blocks[0].Exercises[0].Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps)));

        // Execution continues on the snapshot.
        Assert.Equal(1, (await RecordAsync(client, session.Id, stored.Blocks[0].Exercises[0].Sets[0].Id, 8, 80m)).CompletedSetCount);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Sessions}/{session.Id}/finish", null)).StatusCode);
    }

    // ---- Ownership ----

    [Fact]
    public async Task AnotherUsersWorkout_Is404_ForEveryOperation_AndIsNotChanged()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var workout = await SingleWorkoutAsync(userA, sets: 2, rest: 90);
        var session = await StartAsync(userA, workout);
        var setId = session.Blocks[0].Exercises[0].Sets[0].Id;

        var responses = new[]
        {
            await userB.GetAsync($"{Sessions}/{session.Id}"),
            await userB.PutAsJsonAsync($"{Sessions}/{session.Id}/sets/{setId}", new RecordWorkoutSetRequest(8, 80m)),
            await userB.PostAsync($"{Sessions}/{session.Id}/finish", null),
            await userB.DeleteAsync($"{Sessions}/{session.Id}"),
            // Starting user A's workout as user B.
            await userB.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(workout.ProgramId, workout.WorkoutId))
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal(HttpStatusCode.NoContent, (await userB.GetAsync($"{Sessions}/current")).StatusCode);

        var stored = (await userA.GetFromJsonAsync<WorkoutSessionResponse>($"{Sessions}/{session.Id}"))!;
        Assert.Equal(("InProgress", 0), (stored.Status, stored.CompletedSetCount));
    }

    [Fact]
    public async Task SessionEndpoints_RequireAnAccessToken()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(id, id)),
            await client.GetAsync($"{Sessions}/current"),
            await client.GetAsync($"{Sessions}/{id}"),
            await client.PutAsJsonAsync($"{Sessions}/{id}/sets/{id}", new RecordWorkoutSetRequest(8, null)),
            await client.PostAsync($"{Sessions}/{id}/finish", null),
            await client.DeleteAsync($"{Sessions}/{id}")
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    // ---- Validation ----

    [Theory]
    [InlineData(0, null, "actualReps")]
    [InlineData(1000, null, "actualReps")]
    [InlineData(8, "0", "weightKg")]
    [InlineData(8, "-2.5", "weightKg")]
    [InlineData(8, "1000.5", "weightKg")]
    [InlineData(8, "80.125", "weightKg")]
    public async Task RecordSet_WithInvalidValues_Is400_AndNothingIsStored(int reps, string? weight, string field)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var session = await StartAsync(client, await SingleWorkoutAsync(client, sets: 1, rest: 90));
        var setId = session.Blocks[0].Exercises[0].Sets[0].Id;
        var body = $"{{\"actualReps\":{reps},\"weightKg\":{weight ?? "null"}}}";

        var response = await client.PutAsync(
            $"{Sessions}/{session.Id}/sets/{setId}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"{field}\"", content);
        Assert.DoesNotContain("Parameter", content);
        Assert.Equal(0, (await client.GetFromJsonAsync<WorkoutSessionResponse>($"{Sessions}/{session.Id}"))!.CompletedSetCount);
    }

    [Fact]
    public async Task UnknownSetOrWorkout_Is404_AndAnEmptyWorkout_Is400()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var workout = await SingleWorkoutAsync(client, sets: 1, rest: 90);

        var emptyProgram = await AddWorkoutAsync(client, (await CreateProgramAsync(client)).Id, "Empty");
        var empty = await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(emptyProgram.Id, emptyProgram.Workouts[0].Id));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("\"workoutId\"", await empty.Content.ReadAsStringAsync());

        var unknownWorkout = await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(workout.ProgramId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, unknownWorkout.StatusCode);

        var session = await StartAsync(client, workout);
        var unknownSet = await client.PutAsJsonAsync($"{Sessions}/{session.Id}/sets/{Guid.NewGuid()}", new RecordWorkoutSetRequest(8, null));
        Assert.Equal(HttpStatusCode.NotFound, unknownSet.StatusCode);
        Assert.Contains("Set not found.", await unknownSet.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Sessions}/{Guid.NewGuid()}")).StatusCode);
    }

    // ---- Helpers ----

    // A "Push" workout with one Single bench-press block of sets × 8 reps.
    private static async Task<(Guid ProgramId, Guid WorkoutId)> SingleWorkoutAsync(HttpClient client, int sets, int rest)
    {
        var program = await AddWorkoutAsync(client, (await CreateProgramAsync(client)).Id, "Push");
        var bench = await CreateExerciseAsync(client, $"Bench press {Guid.NewGuid():N}");
        await AddBlockAsync(client, program.Id, program.Workouts[0].Id, new CreateWorkoutBlockRequest("Single", rest, [Exercise(bench.Id, sets, 8)]));

        return (program.Id, program.Workouts[0].Id);
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
