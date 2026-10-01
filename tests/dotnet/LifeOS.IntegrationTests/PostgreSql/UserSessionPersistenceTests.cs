using LifeOS.Application.Authentication;
using LifeOS.Application.Authentication.RefreshSession;
using LifeOS.Application.Authentication.StartSession;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class UserSessionPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly UserSessionOptions Options = new(Lifetime);

    // ---- Test 8: unique refresh-token hash ----

    [Fact]
    public async Task DuplicateRefreshTokenHash_IsRejected()
    {
        var user = await NewUserAsync();
        var hash = NewHash();
        await PostgresAssert.InsertAsync(fixture, UserSession.Start(user.Id, hash, Now, Lifetime));

        await PostgresAssert.InsertViolatesAsync(
            fixture,
            PostgresAssert.UniqueViolation,
            "ux_user_sessions_refresh_token_hash",
            UserSession.Start(user.Id, hash, Now, Lifetime));
    }

    [Fact]
    public async Task DifferentRefreshTokenHashes_AreAccepted()
    {
        var user = await NewUserAsync();

        await PostgresAssert.InsertAsync(
            fixture,
            UserSession.Start(user.Id, NewHash(), Now, Lifetime),
            UserSession.Start(user.Id, NewHash(), Now, Lifetime));
    }

    // ---- Test 9: rotation is atomic under concurrency ----

    [Fact]
    public async Task ConcurrentRotationsOfTheSameSession_ExactlyOneWins()
    {
        var user = await NewUserAsync();
        var original = UserSession.Start(user.Id, NewHash(), Now, Lifetime);
        await PostgresAssert.InsertAsync(fixture, original);

        await using var firstScope = fixture.CreateScope();
        await using var secondScope = fixture.CreateScope();
        var attempts = new List<(IUserSessionRepository Repository, UserSession Rotated, UserSession Replacement)>();

        // Each competing request reads the active session and prepares its own rotation.
        foreach (var scope in new[] { firstScope, secondScope })
        {
            var repository = scope.ServiceProvider.GetRequiredService<IUserSessionRepository>();
            var loaded = (await repository.GetByRefreshTokenHashAsync(original.RefreshTokenHash, CancellationToken.None))!;
            attempts.Add((repository, loaded, loaded.RotateTo(NewHash(), Now.AddMinutes(1), Lifetime)));
        }

        var results = await Task.WhenAll(attempts.Select(attempt =>
            attempt.Repository.TryRotateAsync(attempt.Rotated, attempt.Replacement, CancellationToken.None)));

        Assert.Single(results, rotated => rotated);
        var winningReplacement = attempts[Array.IndexOf(results, true)].Replacement;

        // Final persisted state, from a fresh scope.
        await using var verify = fixture.CreateScope();
        var family = await Sessions(verify).Where(session => session.FamilyId == original.FamilyId).ToListAsync();
        Assert.Equal(2, family.Count);

        var storedOriginal = family.Single(session => session.Id == original.Id);
        Assert.NotNull(storedOriginal.RevokedAtUtc);
        Assert.Equal(winningReplacement.Id, storedOriginal.ReplacedBySessionId);

        var active = Assert.Single(family, session => session.IsActive(Now.AddMinutes(2)));
        Assert.Equal(winningReplacement.Id, active.Id);
    }

    // ---- Test 10: reuse of a rotated token revokes the family ----

    [Fact]
    public async Task ReusingARotatedToken_RevokesTheWholeFamily()
    {
        var user = await NewUserAsync();
        var started = await RunAsync(scope => StartHandler(scope, Now).HandleAsync(user.Id, CancellationToken.None));
        var rotated = await RunAsync(scope => RefreshHandler(scope, Now.AddMinutes(20)).HandleAsync(started.RefreshToken, CancellationToken.None));
        Assert.Equal(RefreshSessionStatus.Refreshed, rotated.Status);

        var reuse = await RunAsync(scope => RefreshHandler(scope, Now.AddMinutes(30)).HandleAsync(started.RefreshToken, CancellationToken.None));
        Assert.Equal(RefreshSessionStatus.Rejected, reuse.Status);

        await using (var verify = fixture.CreateScope())
        {
            var family = await Sessions(verify).Where(session => session.UserId == user.Id).ToListAsync();
            Assert.Equal(2, family.Count);
            Assert.All(family, session => Assert.NotNull(session.RevokedAtUtc));
            Assert.DoesNotContain(family, session => session.IsActive(Now.AddMinutes(31)));
            Assert.Equal(Assert.Single(family.Select(session => session.FamilyId).Distinct()), reuse.RevokedFamilyId);
        }

        var afterReuse = await RunAsync(scope => RefreshHandler(scope, Now.AddMinutes(40)).HandleAsync(rotated.RefreshToken, CancellationToken.None));
        Assert.Equal(RefreshSessionStatus.Rejected, afterReuse.Status);
    }

    private async Task<T> RunAsync<T>(Func<AsyncServiceScope, Task<T>> request)
    {
        await using var scope = fixture.CreateScope();

        return await request(scope);
    }

    private static StartSessionHandler StartHandler(AsyncServiceScope scope, DateTimeOffset now) =>
        new(scope.ServiceProvider.GetRequiredService<IUserSessionRepository>(), Options, new FixedTimeProvider(now));

    private static RefreshSessionHandler RefreshHandler(AsyncServiceScope scope, DateTimeOffset now) =>
        new(scope.ServiceProvider.GetRequiredService<IUserSessionRepository>(), Options, new FixedTimeProvider(now));

    private static IQueryable<UserSession> Sessions(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().UserSessions.AsNoTracking();

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static string NewHash() => RefreshTokens.Hash(RefreshTokens.Generate());
}
