using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Gym.Exercises;
using LifeOS.Contracts.Gym.Programs;
using LifeOS.Contracts.Gym.Sessions;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.IntegrationTests.Http;

// Choosing a workout to train through the real pipeline: the read-only training list, its ownership,
// and that its choices start through the existing workout session endpoint.
public class GymTrainingHttpTests
{
    private const string Programs = "/api/gym/programs";
    private const string Sessions = "/api/gym/sessions";
    private const string Training = "/api/gym/training/programs";

    [Fact]
    public async Task Training_ListsWorkoutsByProgram_AndStartsThroughTheSessionEndpoint()
    {
        await using var factory = new LifeOSApiFactory();
        var client = await SignInAsync(factory, "user-a");

        Assert.Empty((await client.GetFromJsonAsync<List<TrainingProgramResponse>>(Training))!);

        var program = await CreateProgramAsync(client, "Hypertrophy");
        program = await AddWorkoutAsync(client, program.Id, "Push");
        program = await AddWorkoutAsync(client, program.Id, "Empty");
        var push = program.Workouts[0];
        var bench = await CreateExerciseAsync(client, "Bench press");
        var row = await CreateExerciseAsync(client, "Row");
        var curl = await CreateExerciseAsync(client, "Curl");
        await AddBlockAsync(client, program.Id, push.Id, new CreateWorkoutBlockRequest("Single", 90, [Exercise(bench.Id, 3)]));
        await AddBlockAsync(client, program.Id, push.Id, new CreateWorkoutBlockRequest("Superset", 120, [Exercise(row.Id, 3), Exercise(curl.Id, 2)]));
        await CreateProgramAsync(client, "Strength");

        var training = (await client.GetFromJsonAsync<List<TrainingProgramResponse>>(Training))!;

        Assert.Equal(["Hypertrophy", "Strength"], training.Select(item => item.Name));
        Assert.Equal(program.Id, training[0].Id);
        Assert.Equal(
            [(push.Id, "Push", 1, 3, 8, true), (program.Workouts[1].Id, "Empty", 2, 0, 0, false)],
            training[0].Workouts.Select(workout => (workout.Id, workout.Name, workout.Position, workout.ExerciseCount, workout.PrescribedSetCount, workout.CanStart)));
        Assert.Empty(training[1].Workouts);

        // The empty workout is refused by the start, as the list says.
        var empty = await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(program.Id, program.Workouts[1].Id));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var started = await client.PostAsJsonAsync(Sessions, new StartWorkoutSessionRequest(program.Id, push.Id));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var session = (await started.Content.ReadFromJsonAsync<WorkoutSessionResponse>())!;
        Assert.Equal(("Push", 8), (session.WorkoutName, session.PrescribedSetCount));

        // Training a workout changes nothing in the list.
        var after = (await client.GetFromJsonAsync<List<TrainingProgramResponse>>(Training))!;
        Assert.Equal(training.SelectMany(item => item.Workouts), after.SelectMany(item => item.Workouts));
    }

    [Fact]
    public async Task Training_ShowsOnlyTheCallersPrograms()
    {
        await using var factory = new LifeOSApiFactory();
        var userA = await SignInAsync(factory, "user-a");
        var userB = await SignInAsync(factory, "user-b");
        var programA = await AddWorkoutAsync(userA, (await CreateProgramAsync(userA, "A's program")).Id, "A's workout");
        await AddWorkoutAsync(userB, (await CreateProgramAsync(userB, "B's program")).Id, "B's workout");

        var trainingA = (await userA.GetFromJsonAsync<List<TrainingProgramResponse>>(Training))!;
        var trainingB = (await userB.GetFromJsonAsync<List<TrainingProgramResponse>>(Training))!;

        Assert.Equal([(programA.Id, "A's program")], trainingA.Select(program => (program.Id, program.Name)));
        Assert.Equal(["A's workout"], trainingA.SelectMany(program => program.Workouts).Select(workout => workout.Name));
        Assert.Equal(["B's workout"], trainingB.SelectMany(program => program.Workouts).Select(workout => workout.Name));
    }

    [Fact]
    public async Task Training_RequiresSignIn()
    {
        await using var factory = new LifeOSApiFactory();

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Training)).StatusCode);
    }

    // ---- Helpers ----

    private static WorkoutBlockExerciseRequest Exercise(Guid exerciseId, int sets) =>
        new(exerciseId, null, Enumerable.Repeat(new WorkoutSetRequest(8, 8), sets).ToList());

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
