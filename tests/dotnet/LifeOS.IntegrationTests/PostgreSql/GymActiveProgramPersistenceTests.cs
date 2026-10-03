using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Programs.Workouts;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Application.Gym.Training;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Gym.Training;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// GYM-004 against real PostgreSQL: the active program and its completions round-trip, finishing a
// workout stores the session and its cycle progress in one transaction, at most one Active program
// exists per user, deleting the template ends its active program but never executed workouts, and
// the schema backstops hold. Final state is always read from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class GymActiveProgramPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FinishingWorkouts_StoresProgress_AndOpensTheNextCycle()
    {
        var (user, program) = await ProgramAsync("Day 1", "Day 2");
        var active = await ActivateAsync(program, 2);

        var first = await TrainAsync(user.Id, program, program.Workouts[1]);

        var loaded = (await LoadActiveAsync(user.Id))!;
        var completion = Assert.Single(loaded.Completions);
        Assert.Equal((active.Id, 1, program.Workouts[1].Id, first.Id, first.CompletedAtUtc!.Value),
            (completion.ActiveProgramId, completion.Cycle, completion.WorkoutTemplateId, completion.WorkoutSessionId, completion.CompletedAtUtc));
        Assert.Equal((1, ActiveProgramStatus.Active), (loaded.CurrentCycle, loaded.Status));

        await TrainAsync(user.Id, program, program.Workouts[0]);

        loaded = (await LoadActiveAsync(user.Id))!;
        Assert.Equal(2, loaded.CurrentCycle);
        Assert.Empty(loaded.CurrentCycleCompletions);

        await TrainAsync(user.Id, program, program.Workouts[0]);
        await TrainAsync(user.Id, program, program.Workouts[1]);

        Assert.Null(await LoadActiveAsync(user.Id));
        Assert.Equal("Completed 2 4", await ScalarAsync<string>(
            $"""
            SELECT p.status || ' ' || p.current_cycle || ' ' || (SELECT count(*) FROM active_program_completions c WHERE c.active_program_id = p.id) AS "Value"
            FROM active_programs p WHERE p.id = {active.Id}
            """));

        await using var scope = fixture.CreateScope();
        var details = await new GetActiveProgramHandler(Active(scope), Programs(scope)).HandleAsync(user.Id, CancellationToken.None);
        Assert.Null(details);
    }

    [Fact]
    public async Task GetActiveProgram_ReadsToDoAndDone_FromTheTemplateCounts()
    {
        var (user, program) = await ProgramAsync("Day 1", "Day 2", "Day 3");
        await ActivateAsync(program, 3);
        await TrainAsync(user.Id, program, program.Workouts[2]);

        await using var scope = fixture.CreateScope();
        var details = (await new GetActiveProgramHandler(Active(scope), Programs(scope)).HandleAsync(user.Id, CancellationToken.None))!;

        Assert.Equal(("Program", 3, 1), (details.ProgramName, details.TotalCycles, details.CurrentCycle));
        Assert.Equal([("Day 1", 1, 1, true), ("Day 2", 1, 1, true)], details.ToDo.Select(workout => (workout.Name, workout.ExerciseCount, workout.PrescribedSetCount, workout.CanStart)));
        Assert.Equal(["Day 3"], details.Done.Select(workout => workout.Name));
    }

    [Fact]
    public async Task AtMostOneActiveProgramPerUser()
    {
        var (user, program) = await ProgramAsync("Day 1");
        var first = await ActivateAsync(program, 3);

        await using (var scope = fixture.CreateScope())
        {
            Assert.False(await Active(scope).TryAddAsync(ActiveProgram.Activate(program, 3, DateTimeOffset.UtcNow), CancellationToken.None));
        }

        // Stopped: another may be activated.
        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await new StopActiveProgramHandler(Active(scope), TimeProvider.System).HandleAsync(user.Id, CancellationToken.None));
        }

        var second = await ActivateAsync(program, 2);
        Assert.Equal(second.Id, (await LoadActiveAsync(user.Id))!.Id);

        await PostgresAssert.ViolatesAsync(
            PostgresAssert.UniqueViolation,
            "ux_active_programs_user_active",
            () => ExecuteAsync($"UPDATE active_programs SET status = 'Active', ended_at_utc = NULL WHERE id = {first.Id}"));
    }

    [Fact]
    public async Task DeletingTheTemplate_EndsItsActiveProgram_AndKeepsExecutedWorkouts()
    {
        var (user, program) = await ProgramAsync("Day 1", "Day 2");
        var active = await ActivateAsync(program, 3);
        var session = await TrainAsync(user.Id, program, program.Workouts[0]);

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Programs(scope).DeleteAsync(user.Id, program.Id, CancellationToken.None));
        }

        Assert.Null(await LoadActiveAsync(user.Id));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT count(*)::int AS \"Value\" FROM active_programs WHERE id = {active.Id}"));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT count(*)::int AS \"Value\" FROM active_program_completions WHERE active_program_id = {active.Id}"));

        await using var verify = fixture.CreateScope();
        Assert.Equal(WorkoutSessionStatus.Completed, (await Sessions(verify).GetAsync(user.Id, session.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task DeletingTheLastWorkoutToDo_AdvancesTheCycle_InTheSameChange()
    {
        var (user, program) = await ProgramAsync("Day 1", "Day 2");
        await ActivateAsync(program, 3);
        await TrainAsync(user.Id, program, program.Workouts[0]);

        await using (var scope = fixture.CreateScope())
        {
            var result = await new DeleteWorkoutHandler(Programs(scope), Exercises(scope), Progress(scope))
                .HandleAsync(user.Id, program.Id, program.Workouts[1].Id, CancellationToken.None);
            Assert.Equal(WorkoutProgramEditStatus.Updated, result.Status);
        }

        var loaded = (await LoadActiveAsync(user.Id))!;
        Assert.Equal(2, loaded.CurrentCycle);

        await using var verify = fixture.CreateScope();
        Assert.Single((await Programs(verify).GetAsync(user.Id, program.Id, CancellationToken.None))!.Workouts);
    }

    [Fact]
    public async Task Schema_RejectsInvalidProgress()
    {
        var (user, program) = await ProgramAsync("Day 1", "Day 2");
        var active = await ActivateAsync(program, 3);
        var session = await TrainAsync(user.Id, program, program.Workouts[0]);
        var other = await NewUserAsync();

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_active_programs_cycles",
            () => ExecuteAsync($"UPDATE active_programs SET current_cycle = 4 WHERE id = {active.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_active_programs_ended",
            () => ExecuteAsync($"UPDATE active_programs SET status = 'Completed', ended_at_utc = now() WHERE id = {active.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "ux_active_program_completions_cycle_workout",
            () => ExecuteAsync(
                $"""
                INSERT INTO active_program_completions (id, active_program_id, user_id, cycle, workout_template_id, workout_session_id, completed_at_utc)
                SELECT {Guid.NewGuid()}, active_program_id, user_id, cycle, workout_template_id, {Guid.NewGuid()}, completed_at_utc
                FROM active_program_completions WHERE workout_session_id = {session.Id}
                """));
        await PostgresAssert.ViolatesOneOfAsync(PostgresAssert.ForeignKeyViolation,
            ["FK_active_program_completions_active_programs", "FK_active_program_completions_workout_sessions"],
            () => ExecuteAsync($"UPDATE active_program_completions SET user_id = {other.Id} WHERE workout_session_id = {session.Id}"));
        await PostgresAssert.DeleteBlockedAsync("FK_active_program_completions_workout_sessions",
            () => ExecuteAsync($"DELETE FROM workout_sessions WHERE id = {session.Id}"));
    }

    // ---- Helpers ----

    // A user with a program whose workouts each have one Single bench block (1 × 8).
    private async Task<(User User, WorkoutProgram Program)> ProgramAsync(params string[] workouts)
    {
        var user = await NewUserAsync();
        var bench = Exercise.Create(user.Id, "Bench press", Now);
        await PostgresAssert.InsertAsync(fixture, bench);

        var program = WorkoutProgram.Create(user.Id, "Program", Now);

        foreach (var name in workouts)
        {
            program.AddWorkout(name).AddBlock(WorkoutBlockKind.Single, 90, [new ExercisePrescription(bench, null, [new RepRange(8, 8)])]);
        }

        await using var scope = fixture.CreateScope();
        await Programs(scope).AddAsync(program, CancellationToken.None);

        return (user, program);
    }

    private async Task<ActiveProgram> ActivateAsync(WorkoutProgram program, int cycles)
    {
        // Before the real clock the handlers end it with (ended_at_utc >= activated_at_utc).
        var active = ActiveProgram.Activate(program, cycles, DateTimeOffset.UtcNow.AddHours(-1));

        await using var scope = fixture.CreateScope();
        Assert.True(await Active(scope).TryAddAsync(active, CancellationToken.None));

        return active;
    }

    // Starts the workout and finishes it through the real finish handler.
    private async Task<WorkoutSession> TrainAsync(Guid userId, WorkoutProgram program, WorkoutTemplate workout)
    {
        var session = WorkoutSession.Start(program, workout, DateTimeOffset.UtcNow);

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Sessions(scope).TryAddAsync(session, CancellationToken.None));
        }

        await using (var scope = fixture.CreateScope())
        {
            var result = await new FinishWorkoutSessionHandler(Sessions(scope), Exercises(scope), Progress(scope), TimeProvider.System)
                .HandleAsync(userId, session.Id, CancellationToken.None);
            Assert.Equal(WorkoutSessionChangeStatus.Changed, result.Status);
        }

        await using var verify = fixture.CreateScope();

        return (await Sessions(verify).GetAsync(userId, session.Id, CancellationToken.None))!;
    }

    private async Task<ActiveProgram?> LoadActiveAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();

        return await Active(scope).GetActiveAsync(userId, CancellationToken.None);
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task<T> ScalarAsync<T>(FormattableString sql)
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Database.SqlQuery<T>(sql).SingleAsync();
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlAsync(sql);
    }

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static IActiveProgramRepository Active(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IActiveProgramRepository>();

    private static IWorkoutSessionRepository Sessions(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkoutSessionRepository>();

    private static IWorkoutProgramRepository Programs(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkoutProgramRepository>();

    private static IExerciseRepository Exercises(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IExerciseRepository>();

    private static ActiveProgramProgress Progress(AsyncServiceScope scope) =>
        new(Active(scope), Programs(scope), TimeProvider.System);
}
