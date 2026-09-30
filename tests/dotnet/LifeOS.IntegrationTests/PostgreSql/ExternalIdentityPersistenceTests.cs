using LifeOS.Application.Users;
using LifeOS.Application.Users.SignInWithExternalIdentity;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class ExternalIdentityPersistenceTests(PostgreSqlFixture fixture)
{
    private const string ProviderSubjectIndex = "ux_external_identities_provider_subject";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    // ---- Test 1: unique (provider, subject) ----

    [Fact]
    public async Task SameProviderAndSubject_IsRejectedByTheDatabase()
    {
        var subject = RandomSubject();
        var first = NewUser();
        var second = NewUser();
        await PostgresAssert.InsertAsync(fixture, first, ExternalIdentity.Create(first.Id, "google", subject, null, Now));

        await PostgresAssert.InsertViolatesAsync(
            fixture,
            PostgresAssert.UniqueViolation,
            ProviderSubjectIndex,
            second,
            ExternalIdentity.Create(second.Id, "google", subject, null, Now));
    }

    [Fact]
    public async Task SameProviderWithDifferentSubject_IsAllowed()
    {
        var first = NewUser();
        var second = NewUser();

        await PostgresAssert.InsertAsync(
            fixture,
            first, ExternalIdentity.Create(first.Id, "google", RandomSubject(), null, Now),
            second, ExternalIdentity.Create(second.Id, "google", RandomSubject(), null, Now));
    }

    [Fact]
    public async Task DifferentProviderWithSameSubject_IsAllowed()
    {
        var subject = RandomSubject();
        var first = NewUser();
        var second = NewUser();

        await PostgresAssert.InsertAsync(
            fixture,
            first, ExternalIdentity.Create(first.Id, "google", subject, null, Now),
            second, ExternalIdentity.Create(second.Id, "dev", subject, null, Now));
    }

    [Fact]
    public async Task SameEmailWithDifferentSubjects_IsAllowed()
    {
        var email = $"{RandomSubject()}@example.com";
        var first = User.CreateFromExternalIdentity(null, email, Now);
        var second = User.CreateFromExternalIdentity(null, email, Now);

        await PostgresAssert.InsertAsync(
            fixture,
            first, ExternalIdentity.Create(first.Id, "google", RandomSubject(), email, Now),
            second, ExternalIdentity.Create(second.Id, "google", RandomSubject(), email, Now));
    }

    // ---- Test 2: first sign-in race and 23505 translation ----

    [Fact]
    public async Task ConcurrentTryAdd_OneWins_LoserGetsFalseWithoutOrphanAndCanContinue()
    {
        var subject = RandomSubject();
        var candidates = new[] { NewUser(), NewUser() };

        await using var firstScope = fixture.CreateScope();
        await using var secondScope = fixture.CreateScope();
        var scopes = new[] { firstScope, secondScope };

        var results = await Task.WhenAll(scopes.Select((scope, index) =>
            Repository(scope).TryAddAsync(
                candidates[index],
                ExternalIdentity.Create(candidates[index].Id, "google", subject, null, Now),
                CancellationToken.None)));

        Assert.Single(results, added => added);
        var winner = candidates[Array.IndexOf(results, true)];
        var loser = candidates[Array.IndexOf(results, false)];
        var loserScope = scopes[Array.IndexOf(results, false)];

        // The loser's failed User/ExternalIdentity are detached: its DbContext can keep working.
        var loserContext = loserScope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        Assert.Equal(0, loserContext.PendingInserts<User>());
        Assert.Equal(0, loserContext.PendingInserts<ExternalIdentity>());
        var followUp = NewUser();
        Assert.True(await Repository(loserScope).TryAddAsync(
            followUp,
            ExternalIdentity.Create(followUp.Id, "google", RandomSubject(), null, Now),
            CancellationToken.None));

        // Final state from a fresh scope.
        await using var verify = fixture.CreateScope();
        var dbContext = verify.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        Assert.False(await dbContext.Users.AnyAsync(user => user.Id == loser.Id));
        Assert.True(await dbContext.Users.AnyAsync(user => user.Id == followUp.Id));
        var identity = await Repository(verify).GetExternalIdentityAsync("google", subject, CancellationToken.None);
        Assert.Equal(winner.Id, identity!.UserId);
    }

    [Fact]
    public async Task TryAdd_WithAnotherUniqueViolation_StillThrows()
    {
        // The same user row inserted again: a primary-key violation, which is also 23505 but not
        // the identity index, so it must propagate instead of being reported as "identity exists".
        var user = NewUser();
        await PostgresAssert.InsertAsync(fixture, user);

        await using var scope = fixture.CreateScope();

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "PK_users", () =>
            Repository(scope).TryAddAsync(
                user,
                ExternalIdentity.Create(user.Id, "google", RandomSubject(), null, Now),
                CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentFirstSignIns_ResolveToTheSameUserThroughTheRealConflictPath()
    {
        var subject = RandomSubject();
        var rendezvous = new Rendezvous(participants: 2);

        await using var firstScope = fixture.CreateScope();
        await using var secondScope = fixture.CreateScope();

        // Both handlers look the identity up, see nothing, and only then continue: both insert, so
        // one must hit the real unique violation, re-read once and resolve to the winner.
        var results = await Task.WhenAll(new[] { firstScope, secondScope }.Select(scope =>
            new SignInWithExternalIdentityHandler(
                    new PausingUserRepository(Repository(scope), rendezvous),
                    new FixedTimeProvider(Now))
                .HandleAsync(
                    new SignInWithExternalIdentityCommand("google", subject, "person@example.com", "Person"),
                    CancellationToken.None)));

        Assert.Single(results, result => result.IsNewUser);
        Assert.Equal(results[0].UserId, results[1].UserId);

        await using var verify = fixture.CreateScope();
        var dbContext = verify.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        var identities = await dbContext.ExternalIdentities
            .Where(identity => identity.Provider == "google" && identity.Subject == subject)
            .ToListAsync();
        Assert.Equal(results[0].UserId, Assert.Single(identities).UserId);
        Assert.True(await dbContext.Users.AnyAsync(user => user.Id == results[0].UserId));
    }

    private static IUserRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IUserRepository>();

    private static User NewUser() => User.CreateFromExternalIdentity(null, null, Now);

    private static string RandomSubject() => "subject-" + Guid.NewGuid().ToString("N");
}
