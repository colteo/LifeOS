using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Gym.Exercises;
using LifeOS.Contracts.Gym.Programs;

namespace LifeOS.IntegrationTests.Http;

// Gym program authoring through the real pipeline (routing, JWT, binding, status mapping).
public class GymHttpTests
{
    private const string Programs = "/api/gym/programs";
    private const string Exercises = "/api/gym/exercises";

    [Fact]
    public async Task AuthoringFlow_ProgramWorkoutsSingleAndSuperset_IsReturnedInOrderOnReload()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var created = await client.PostAsJsonAsync(Programs, new CreateWorkoutProgramRequest(" Hypertrophy "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var program = (await created.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!;
        Assert.Equal(("Hypertrophy", 0), (program.Name, program.Workouts.Count));

        await AddWorkoutAsync(client, program.Id, "Back / Triceps / Legs");
        program = await AddWorkoutAsync(client, program.Id, "Shoulders / Chest / Biceps");
        var push = program.Workouts[1];

        var bench = await CreateExerciseAsync(client, "Bench press");
        var row = await CreateExerciseAsync(client, "Seated row");
        var curl = await CreateExerciseAsync(client, "Curl");

        var single = await client.PostAsJsonAsync(
            BlocksPath(program.Id, push.Id),
            new CreateWorkoutBlockRequest("Single", 90, [new WorkoutBlockExerciseRequest(bench.Id, "Pause at the bottom", [new(12, 12), new(10, 10), new(8, 8)])]));
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);

        var superset = await client.PostAsJsonAsync(
            BlocksPath(program.Id, push.Id),
            new CreateWorkoutBlockRequest("superset", 120, [Exercise(row.Id, 3, 8, 10), Exercise(curl.Id, 3, 12, 12)]));
        Assert.Equal(HttpStatusCode.OK, superset.StatusCode);

        // Reload.
        var reloaded = (await client.GetFromJsonAsync<WorkoutProgramResponse>($"{Programs}/{program.Id}"))!;

        Assert.Equal(["Back / Triceps / Legs", "Shoulders / Chest / Biceps"], reloaded.Workouts.Select(workout => workout.Name));
        var blocks = reloaded.Workouts[1].Blocks;
        Assert.Equal([(1, "Single", (int?)90), (2, "Superset", (int?)120)], blocks.Select(block => (block.Position, block.Kind, block.RestSeconds)));
        Assert.Equal(("Bench press", "Pause at the bottom"), (blocks[0].Exercises[0].ExerciseName, blocks[0].Exercises[0].Notes));
        Assert.Equal([(1, 12, 12), (2, 10, 10), (3, 8, 8)], blocks[0].Exercises[0].Sets.Select(set => (set.Position, set.TargetMinReps, set.TargetMaxReps)));
        Assert.Equal([(1, "Seated row"), (2, "Curl")], blocks[1].Exercises.Select(exercise => (exercise.Position, exercise.ExerciseName)));
        Assert.Equal([(8, 10), (8, 10), (8, 10)], blocks[1].Exercises[0].Sets.Select(set => (set.TargetMinReps, set.TargetMaxReps)));

        var listed = Assert.Single((await client.GetFromJsonAsync<List<WorkoutProgramSummaryResponse>>(Programs))!);
        Assert.Equal((program.Id, "Hypertrophy", 2), (listed.Id, listed.Name, listed.WorkoutCount));
    }

    [Fact]
    public async Task Workouts_RenameReorderDelete()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await CreateProgramAsync(client, "Program");
        await AddWorkoutAsync(client, program.Id, "A");
        await AddWorkoutAsync(client, program.Id, "B");
        program = await AddWorkoutAsync(client, program.Id, "C");
        var (a, b, c) = (program.Workouts[0].Id, program.Workouts[1].Id, program.Workouts[2].Id);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Programs}/{program.Id}/workouts/{b}", new UpdateWorkoutRequest("Legs"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Programs}/{program.Id}/workouts/order", new ReorderWorkoutsRequest([c, a, b]))).StatusCode);
        var deleted = await client.DeleteAsync($"{Programs}/{program.Id}/workouts/{a}");

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var stored = (await client.GetFromJsonAsync<WorkoutProgramResponse>($"{Programs}/{program.Id}"))!;
        Assert.Equal([("C", 1), ("Legs", 2)], stored.Workouts.Select(workout => (workout.Name, workout.Position)));

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Programs}/{program.Id}", new UpdateWorkoutProgramRequest("Renamed"))).StatusCode);
        Assert.Equal("Renamed", (await client.GetFromJsonAsync<WorkoutProgramResponse>($"{Programs}/{program.Id}"))!.Name);
    }

    [Fact]
    public async Task Blocks_UpdateReorderDelete()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await AddWorkoutAsync(client, (await CreateProgramAsync(client, "Program")).Id, "Day 1");
        var workoutId = program.Workouts[0].Id;
        var bench = await CreateExerciseAsync(client, "Bench press");
        var row = await CreateExerciseAsync(client, "Row");
        await client.PostAsJsonAsync(BlocksPath(program.Id, workoutId), new CreateWorkoutBlockRequest("Single", 60, [Exercise(bench.Id, 3, 8, 8)]));
        var withBoth = await (await client.PostAsJsonAsync(
            BlocksPath(program.Id, workoutId),
            new CreateWorkoutBlockRequest("Superset", 90, [Exercise(bench.Id, 3, 8, 8), Exercise(row.Id, 3, 8, 8)]))).Content.ReadFromJsonAsync<WorkoutProgramResponse>();
        var (singleId, supersetId) = (withBoth!.Workouts[0].Blocks[0].Id, withBoth.Workouts[0].Blocks[1].Id);

        var updated = await client.PutAsJsonAsync(
            $"{BlocksPath(program.Id, workoutId)}/{supersetId}",
            new UpdateWorkoutBlockRequest(null, [Exercise(row.Id, 4, 10, 12), new WorkoutBlockExerciseRequest(bench.Id, "Light", [new(15, 15)])]));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var superset = (await updated.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!.Workouts[0].Blocks[1];
        Assert.Null(superset.RestSeconds);
        Assert.Equal([("Row", 4, (string?)null), ("Bench press", 1, "Light")], superset.Exercises.Select(exercise => (exercise.ExerciseName, exercise.Sets.Count, exercise.Notes)));

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{BlocksPath(program.Id, workoutId)}/order", new ReorderWorkoutBlocksRequest([supersetId, singleId]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{BlocksPath(program.Id, workoutId)}/{supersetId}")).StatusCode);

        var remaining = Assert.Single((await client.GetFromJsonAsync<WorkoutProgramResponse>($"{Programs}/{program.Id}"))!.Workouts[0].Blocks);
        Assert.Equal((singleId, 1, "Single"), (remaining.Id, remaining.Position, remaining.Kind));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{BlocksPath(program.Id, workoutId)}/{supersetId}")).StatusCode);
    }

    // ---- Exercises ----

    [Fact]
    public async Task Exercises_DuplicateNameForTheSameUser_Is409_ButAnotherUserMayUseIt()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        await CreateExerciseAsync(userA, "Bench press");

        var duplicate = await userA.PostAsJsonAsync(Exercises, new CreateExerciseRequest("bench PRESS"));
        var otherUser = await userB.PostAsJsonAsync(Exercises, new CreateExerciseRequest("Bench press"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("An exercise with this name already exists.", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, otherUser.StatusCode);
        Assert.Single((await userA.GetFromJsonAsync<List<ExerciseResponse>>(Exercises))!);
        Assert.Single((await userB.GetFromJsonAsync<List<ExerciseResponse>>(Exercises))!);
    }

    [Fact]
    public async Task Exercises_BlankName_Is400()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        var response = await client.PostAsJsonAsync(Exercises, new CreateExerciseRequest("  "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"name\"", await response.Content.ReadAsStringAsync());
    }

    // ---- Ownership ----

    [Fact]
    public async Task AnotherUsersProgram_Is404_ForEveryOperation_AndIsNotChanged()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var program = await AddWorkoutAsync(userA, (await CreateProgramAsync(userA, "Mine")).Id, "Day 1");
        var workoutId = program.Workouts[0].Id;
        var benchOfB = await CreateExerciseAsync(userB, "Bench press");

        var responses = new[]
        {
            await userB.GetAsync($"{Programs}/{program.Id}"),
            await userB.PutAsJsonAsync($"{Programs}/{program.Id}", new UpdateWorkoutProgramRequest("Taken")),
            await userB.PostAsJsonAsync($"{Programs}/{program.Id}/workouts", new CreateWorkoutRequest("Day 2")),
            await userB.PutAsJsonAsync($"{Programs}/{program.Id}/workouts/{workoutId}", new UpdateWorkoutRequest("Taken")),
            await userB.PutAsJsonAsync($"{Programs}/{program.Id}/workouts/order", new ReorderWorkoutsRequest([workoutId])),
            await userB.PostAsJsonAsync(BlocksPath(program.Id, workoutId), new CreateWorkoutBlockRequest("Single", 60, [Exercise(benchOfB.Id, 3, 8, 8)])),
            await userB.DeleteAsync($"{Programs}/{program.Id}/workouts/{workoutId}"),
            await userB.DeleteAsync($"{Programs}/{program.Id}")
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Empty((await userB.GetFromJsonAsync<List<WorkoutProgramSummaryResponse>>(Programs))!);

        var stored = (await userA.GetFromJsonAsync<WorkoutProgramResponse>($"{Programs}/{program.Id}"))!;
        Assert.Equal(("Mine", "Day 1"), (stored.Name, Assert.Single(stored.Workouts).Name));
        Assert.Empty(stored.Workouts[0].Blocks);
    }

    [Fact]
    public async Task AddBlock_WithAnotherUsersExercise_Is404()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var benchOfA = await CreateExerciseAsync(userA, "Bench press");
        var program = await AddWorkoutAsync(userB, (await CreateProgramAsync(userB, "Theirs")).Id, "Day 1");

        var response = await userB.PostAsJsonAsync(
            BlocksPath(program.Id, program.Workouts[0].Id),
            new CreateWorkoutBlockRequest("Single", 60, [Exercise(benchOfA.Id, 3, 8, 8)]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Exercise not found.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GymEndpoints_RequireAnAccessToken()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Programs)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Exercises)).StatusCode);
    }

    // ---- Validation ----

    public static TheoryData<string, object, string> InvalidBlocks => new()
    {
        { "min reps 0", new { kind = "Single", restSeconds = 60, exercises = new[] { new { exerciseId = Guid.Empty, sets = new[] { new { targetMinReps = 0, targetMaxReps = 8 } } } } }, "sets" },
        { "max below min", new { kind = "Single", restSeconds = 60, exercises = new[] { new { exerciseId = Guid.Empty, sets = new[] { new { targetMinReps = 10, targetMaxReps = 8 } } } } }, "sets" },
        { "no sets", new { kind = "Single", restSeconds = 60, exercises = new[] { new { exerciseId = Guid.Empty, sets = Array.Empty<object>() } } }, "sets" },
        { "rest 0", new { kind = "Single", restSeconds = 0, exercises = new[] { new { exerciseId = Guid.Empty, sets = new[] { new { targetMinReps = 8, targetMaxReps = 8 } } } } }, "restSeconds" },
        { "negative rest", new { kind = "Single", restSeconds = -90, exercises = new[] { new { exerciseId = Guid.Empty, sets = new[] { new { targetMinReps = 8, targetMaxReps = 8 } } } } }, "restSeconds" },
        { "superset with one", new { kind = "Superset", restSeconds = 60, exercises = new[] { new { exerciseId = Guid.Empty, sets = new[] { new { targetMinReps = 8, targetMaxReps = 8 } } } } }, "exercises" },
        { "single with two", new { kind = "Single", restSeconds = 60, exercises = new[] { new { exerciseId = Guid.Empty, sets = new[] { new { targetMinReps = 8, targetMaxReps = 8 } } }, new { exerciseId = Guid.Empty, sets = new[] { new { targetMinReps = 8, targetMaxReps = 8 } } } } }, "exercises" },
        { "unknown kind", new { kind = "Circuit", restSeconds = 60, exercises = new[] { new { exerciseId = Guid.Empty, sets = new[] { new { targetMinReps = 8, targetMaxReps = 8 } } } } }, "kind" }
    };

    [Theory]
    [MemberData(nameof(InvalidBlocks))]
    public async Task AddBlock_WithAnInvalidPrescription_Is400_AndNothingIsStored(string scenario, object body, string field)
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await AddWorkoutAsync(client, (await CreateProgramAsync(client, "Program")).Id, "Day 1");
        var bench = await CreateExerciseAsync(client, "Bench press");

        // Every placeholder exercise id becomes the user's real exercise.
        var json = System.Text.Json.JsonSerializer.Serialize(body).Replace(Guid.Empty.ToString(), bench.Id.ToString());
        var response = await client.PostAsync(
            BlocksPath(program.Id, program.Workouts[0].Id),
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, scenario);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
        Assert.Empty((await client.GetFromJsonAsync<WorkoutProgramResponse>($"{Programs}/{program.Id}"))!.Workouts[0].Blocks);
    }

    [Fact]
    public async Task Reorder_WithAStaleList_Is400()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await CreateProgramAsync(client, "Program");
        await AddWorkoutAsync(client, program.Id, "A");
        program = await AddWorkoutAsync(client, program.Id, "B");

        var response = await client.PutAsJsonAsync($"{Programs}/{program.Id}/workouts/order", new ReorderWorkoutsRequest([program.Workouts[1].Id]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"workoutIds\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BlankProgramOrWorkoutName_Is400()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await CreateProgramAsync(client, "Program");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Programs, new CreateWorkoutProgramRequest(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Programs}/{program.Id}/workouts", new CreateWorkoutRequest(""))).StatusCode);
    }

    // ---- Deletion ----

    [Fact]
    public async Task DeleteProgram_Is204_AndKeepsExercises()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await AddWorkoutAsync(client, (await CreateProgramAsync(client, "Program")).Id, "Day 1");
        var bench = await CreateExerciseAsync(client, "Bench press");
        await client.PostAsJsonAsync(BlocksPath(program.Id, program.Workouts[0].Id), new CreateWorkoutBlockRequest("Single", 60, [Exercise(bench.Id, 3, 8, 8)]));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Programs}/{program.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Programs}/{program.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<WorkoutProgramSummaryResponse>>(Programs))!);
        Assert.Equal("Bench press", Assert.Single((await client.GetFromJsonAsync<List<ExerciseResponse>>(Exercises))!).Name);
    }

    // ---- Helpers ----

    private static string BlocksPath(Guid programId, Guid workoutId) => $"{Programs}/{programId}/workouts/{workoutId}/blocks";

    private static WorkoutBlockExerciseRequest Exercise(Guid exerciseId, int sets, int minReps, int maxReps) =>
        new(exerciseId, null, Enumerable.Repeat(new WorkoutSetRequest(minReps, maxReps), sets).ToList());

    private static async Task<WorkoutProgramResponse> CreateProgramAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Programs, new CreateWorkoutProgramRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!;
    }

    private static async Task<WorkoutProgramResponse> AddWorkoutAsync(HttpClient client, Guid programId, string name)
    {
        var response = await client.PostAsJsonAsync($"{Programs}/{programId}/workouts", new CreateWorkoutRequest(name));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!;
    }

    private static async Task<ExerciseResponse> CreateExerciseAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Exercises, new CreateExerciseRequest(name));
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
