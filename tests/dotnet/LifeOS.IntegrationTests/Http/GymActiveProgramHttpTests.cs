using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Gym.Exercises;
using LifeOS.Contracts.Gym.History;
using LifeOS.Contracts.Gym.Programs;
using LifeOS.Contracts.Gym.Sessions;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.IntegrationTests.Http;

// GYM-004 through the real pipeline: activating a program, training its cycles with the GYM-002
// session endpoints, stopping it, ownership and validation.
public class GymActiveProgramHttpTests
{
    private const string Programs = "/api/gym/programs";
    private const string Sessions = "/api/gym/sessions";
    private const string ActiveProgram = "/api/gym/active-program";

    [Fact]
    public async Task ActiveProgram_IsTrainedCycleByCycle_ThroughTheSessionEndpoints()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await ProgramAsync(client, "Day 1", "Day 2");
        var (day1, day2) = (program.Workouts[0].Id, program.Workouts[1].Id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(ActiveProgram)).StatusCode);

        var activated = await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 2));
        Assert.Equal(HttpStatusCode.Created, activated.StatusCode);
        var active = (await activated.Content.ReadFromJsonAsync<ActiveProgramResponse>())!;
        Assert.Equal((program.Id, "Program", 2, 1), (active.ProgramId, active.ProgramName, active.TotalCycles, active.CurrentCycle));
        Assert.Equal([(day1, "Day 1", true), (day2, "Day 2", true)], active.ToDo.Select(workout => (workout.Id, workout.Name, workout.CanStart)));
        Assert.Empty(active.Done);

        var sessionId = await TrainAsync(client, program.Id, day2);

        active = await GetActiveAsync(client);
        Assert.Equal(1, active.CurrentCycle);
        Assert.Equal(["Day 1"], active.ToDo.Select(workout => workout.Name));
        var done = Assert.Single(active.Done);
        Assert.Equal((day2, "Day 2", sessionId), (done.Id, done.Name, done.SessionId));

        await TrainAsync(client, program.Id, day1);

        active = await GetActiveAsync(client);
        Assert.Equal(2, active.CurrentCycle);
        Assert.Equal(["Day 1", "Day 2"], active.ToDo.Select(workout => workout.Name));
        Assert.Empty(active.Done);

        await TrainAsync(client, program.Id, day1);
        await TrainAsync(client, program.Id, day2);

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(ActiveProgram)).StatusCode);

        // History is unchanged by cycles: every finished workout is there.
        var history = (await client.GetFromJsonAsync<WorkoutHistoryPageResponse>("/api/gym/history"))!;
        Assert.Equal(4, history.Items.Count);
    }

    [Fact]
    public async Task Activate_ValidatesCyclesAndProgram_AndRefusesASecondActiveProgram()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await ProgramAsync(client, "Day 1");
        var emptyProgram = await CreateProgramAsync(client, "Empty");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 100))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(emptyProgram.Id, 3))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(Guid.NewGuid(), 3))).StatusCode);

        var first = await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 3));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var active = (await first.Content.ReadFromJsonAsync<ActiveProgramResponse>())!;

        var second = await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 3));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var problem = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(active.Id, problem.RootElement.GetProperty("activeProgramId").GetGuid());
    }

    [Fact]
    public async Task Stop_EndsTheActiveProgram()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");
        var program = await ProgramAsync(client, "Day 1");

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{ActiveProgram}/stop", null)).StatusCode);

        await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 3));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"{ActiveProgram}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(ActiveProgram)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 3))).StatusCode);
    }

    [Fact]
    public async Task ActiveProgram_IsOwnedByTheCaller()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var program = await ProgramAsync(userA, "Day 1");

        Assert.Equal(HttpStatusCode.NotFound, (await userB.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 3))).StatusCode);

        await userA.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(program.Id, 3));

        Assert.Equal(HttpStatusCode.NoContent, (await userB.GetAsync(ActiveProgram)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await userB.PostAsync($"{ActiveProgram}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await userA.GetAsync(ActiveProgram)).StatusCode);
    }

    [Fact]
    public async Task ActiveProgram_RequiresSignIn()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ActiveProgram)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(ActiveProgram, new ActivateProgramRequest(Guid.NewGuid(), 3))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"{ActiveProgram}/stop", null)).StatusCode);
    }

    // ---- Helpers ----

    private static async Task<ActiveProgramResponse> GetActiveAsync(HttpClient client)
    {
        var response = await client.GetAsync(ActiveProgram);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ActiveProgramResponse>())!;
    }

    // Starts and finishes the workout; returns the session id.
    private static async Task<Guid> TrainAsync(HttpClient client, Guid programId, Guid workoutId)
    {
        var started = await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(programId, workoutId));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var session = (await started.Content.ReadFromJsonAsync<WorkoutSessionResponse>())!;

        var finished = await client.PostAsync($"{Sessions}/{session.Id}/finish", null);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);

        return session.Id;
    }

    // A program whose workouts each have one Single bench block.
    private static async Task<WorkoutProgramResponse> ProgramAsync(HttpClient client, params string[] workouts)
    {
        var program = await CreateProgramAsync(client, "Program");
        var bench = await client.PostAsJsonAsync("/api/gym/exercises", new CreateExerciseRequest($"Bench {Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Created, bench.StatusCode);
        var benchId = (await bench.Content.ReadFromJsonAsync<ExerciseResponse>())!.Id;

        foreach (var name in workouts)
        {
            var added = await client.PostAsJsonAsync($"{Programs}/{program.Id}/workouts", new CreateWorkoutRequest(name));
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            program = (await added.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!;
            var workoutId = program.Workouts[^1].Id;

            var block = await client.PostAsJsonAsync(
                $"{Programs}/{program.Id}/workouts/{workoutId}/blocks",
                new CreateWorkoutBlockRequest("Single", 90, [new WorkoutBlockExerciseRequest(benchId, null, [new WorkoutSetRequest(8, 8)])]));
            Assert.Equal(HttpStatusCode.OK, block.StatusCode);
            program = (await block.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!;
        }

        return program;
    }

    private static async Task<WorkoutProgramResponse> CreateProgramAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Programs, new CreateWorkoutProgramRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkoutProgramResponse>())!;
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
