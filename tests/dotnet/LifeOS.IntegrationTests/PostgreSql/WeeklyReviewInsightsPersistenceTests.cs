using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.Users;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AI-001 against real PostgreSQL: the weekly_review_insights schema, the repository (insert-once,
// owner-scoped reads, cascade with the review and the user), the versioned jsonb content, and the
// generation use case end to end with the real repositories and a fake AI port. Every assertion is
// scoped to this test's own users (the database is shared by the PostgreSQL collection).
[Collection(PostgreSqlCollection.Name)]
public class WeeklyReviewInsightsPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateOnly WeekEnd = new(2026, 10, 4);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 7, 30, 0, TimeSpan.Zero);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheInsightsTable_OwnedByTheReview()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddWeeklyReviewInsights", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "review_id uuid NOT NULL", "output_version integer NOT NULL", "content jsonb NOT NULL",
                "provider character varying(100) NOT NULL", "model character varying(100) NOT NULL",
                "prompt_version character varying(100) NOT NULL", "generated_at_utc timestamp with time zone NOT NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'weekly_review_insights'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
                """));
        Assert.Equal(
            ["CREATE UNIQUE INDEX \"PK_weekly_review_insights\" ON public.weekly_review_insights USING btree (review_id)"],
            await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'weekly_review_insights'"));
        Assert.Equal(
            ["FK_weekly_review_insights_weekly_reviews_review_id c", "ck_weekly_review_insights_content", "ck_weekly_review_insights_output_version"],
            (await Strings(database,
                """
                SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text ELSE '' END AS "Value" FROM pg_constraint
                WHERE conrelid = 'weekly_review_insights'::regclass AND contype IN ('c', 'f')
                """)).Order(StringComparer.Ordinal));

        // The deterministic table is unchanged by AI-001.
        Assert.Equal(8, await database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_attribute WHERE attrelid = 'weekly_reviews'::regclass AND attnum > 0 AND NOT attisdropped").SingleAsync());
    }

    [Fact]
    public async Task InvalidRows_AreRejectedByTheDatabase()
    {
        var review = await ReviewAsync(await NewUserAsync());
        Assert.True(await AddAsync(Insights(review.Id)));

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_weekly_review_insights_output_version", () =>
            Execute($"UPDATE weekly_review_insights SET output_version = 0 WHERE review_id = '{review.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_weekly_review_insights_content", () =>
            Execute($"UPDATE weekly_review_insights SET content = '[]'::jsonb WHERE review_id = '{review.Id}'"));
    }

    // ---- Repository ----

    [Fact]
    public async Task TryAdd_IsOncePerReview_AndNeverReplaces()
    {
        var review = await ReviewAsync(await NewUserAsync());
        var first = Insights(review.Id, "First.");

        Assert.True(await AddAsync(first));
        Assert.False(await AddAsync(Insights(review.Id, "Second.")));

        await using var scope = fixture.CreateScope();
        Assert.Equal("First.", (await Repository(scope).GetAsync(review.UserId, review.Id, default))!.Content.Summary);
    }

    [Fact]
    public async Task ConcurrentInserts_StoreExactlyOneLogicalInsights()
    {
        var review = await ReviewAsync(await NewUserAsync());

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => AddAsync(Insights(review.Id, $"Attempt {index}."))));

        Assert.Single(results, added => added);
        Assert.Equal(1, await CountAsync(review.Id));
    }

    [Fact]
    public async Task ForAMissingReview_NothingIsStored()
    {
        Assert.False(await AddAsync(Insights(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task Content_RoundTrips_AsAVersionedJsonbDocument_WithItsIdentity()
    {
        var review = await ReviewAsync(await NewUserAsync());
        var saved = Insights(review.Id);
        Assert.True(await AddAsync(saved));

        await using var scope = fixture.CreateScope();
        var read = (await Repository(scope).GetAsync(review.UserId, review.Id, default))!;

        Assert.Equal((saved.ReviewId, 1, saved.Generation, saved.GeneratedAtUtc), (read.ReviewId, read.OutputVersion, read.Generation, read.GeneratedAtUtc));
        Assert.Equal(saved.Content.Summary, read.Content.Summary);
        Assert.Equal(saved.Content.Wins, read.Content.Wins);
        Assert.Equal(saved.Content.Attention, read.Content.Attention);
        Assert.Equal(saved.Content.Patterns, read.Content.Patterns);
        Assert.Equal(saved.Content.NextWeekFocus, read.Content.NextWeekFocus);

        var document = await Db(scope).Database.SqlQuery<string>($"SELECT content::text AS \"Value\" FROM weekly_review_insights WHERE review_id = {review.Id}").SingleAsync();
        Assert.Contains("\"summary\":", document);
        Assert.Contains("\"nextWeekFocus\": [", document);
        Assert.DoesNotContain("groq", document);
    }

    [Fact]
    public async Task AnUnknownOutputVersion_IsNeverGuessed()
    {
        var review = await ReviewAsync(await NewUserAsync());
        Assert.True(await AddAsync(Insights(review.Id)));
        await Execute($"UPDATE weekly_review_insights SET output_version = 99 WHERE review_id = '{review.Id}'");

        await using var scope = fixture.CreateScope();
        await Assert.ThrowsAsync<NotSupportedException>(() => Repository(scope).GetAsync(review.UserId, review.Id, default));
    }

    [Fact]
    public async Task Reads_AreScopedToTheReviewsOwner()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var review = await ReviewAsync(owner);
        Assert.True(await AddAsync(Insights(review.Id)));

        await using var scope = fixture.CreateScope();
        Assert.NotNull(await Repository(scope).GetAsync(owner.Id, review.Id, default));
        Assert.Null(await Repository(scope).GetAsync(other.Id, review.Id, default));
    }

    [Fact]
    public async Task DeletingTheUser_DeletesTheReviewAndItsInsights()
    {
        var user = await NewUserAsync();
        var review = await ReviewAsync(user);
        Assert.True(await AddAsync(Insights(review.Id)));

        await Execute($"DELETE FROM users WHERE id = '{user.Id}'");

        Assert.Equal(0, await CountAsync(review.Id));
    }

    // ---- Generation end to end (real repositories, fake AI port) ----

    [Fact]
    public async Task Generate_StoresOnce_AndNeverChangesTheDeterministicReview()
    {
        var user = await NewUserAsync();
        var review = await ReviewAsync(user);
        var before = await ReviewRowAsync(review.Id);
        var ai = new FakeWeeklyReviewInterpreter();

        var first = await GenerateAsync(ai, user.Id, review.Id);
        var second = await GenerateAsync(ai, user.Id, review.Id);

        Assert.Equal((WeeklyReviewInsightsStatus.Available, WeeklyReviewInsightsStatus.Available), (first.Status, second.Status));
        Assert.Single(ai.Inputs);
        Assert.Equal(1, await CountAsync(review.Id));
        Assert.Equal(before, await ReviewRowAsync(review.Id));
    }

    [Fact]
    public async Task Generate_Failures_StoreNothing_AndLeaveTheReviewReadable()
    {
        var user = await NewUserAsync();
        var review = await ReviewAsync(user);
        var before = await ReviewRowAsync(review.Id);

        var unavailable = await GenerateAsync(new FakeWeeklyReviewInterpreter { Respond = _ => FakeWeeklyReviewInterpreter.Unavailable }, user.Id, review.Id);
        var invalid = await GenerateAsync(new FakeWeeklyReviewInterpreter
        {
            Respond = _ => FakeWeeklyReviewInterpreter.Success(FakeWeeklyReviewInterpreter.Content with { Wins = ["a", "b", "c", "d"] })
        }, user.Id, review.Id);

        Assert.Equal((WeeklyReviewInsightsStatus.Unavailable, WeeklyReviewInsightsStatus.Failed), (unavailable.Status, invalid.Status));
        Assert.Equal(0, await CountAsync(review.Id));
        Assert.Equal(before, await ReviewRowAsync(review.Id));

        await using var scope = fixture.CreateScope();
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IWeeklyReviewRepository>().GetAsync(user.Id, review.Id, default));
    }

    // ---- Helpers ----

    private async Task<WeeklyReviewInsightsResult> GenerateAsync(FakeWeeklyReviewInterpreter ai, Guid userId, Guid reviewId)
    {
        await using var scope = fixture.CreateScope();
        var handler = new GenerateWeeklyReviewInsightsHandler(
            scope.ServiceProvider.GetRequiredService<IWeeklyReviewRepository>(), Repository(scope), ai, new FixedTimeProvider(Now));

        return await handler.HandleAsync(userId, reviewId, default);
    }

    private static WeeklyReviewInsights Insights(Guid reviewId, string summary = "A steady week.") =>
        WeeklyReviewInsights.Create(reviewId, FakeWeeklyReviewInterpreter.Content with { Summary = summary }, FakeWeeklyReviewInterpreter.Identity, Now);

    private async Task<bool> AddAsync(WeeklyReviewInsights insights)
    {
        await using var scope = fixture.CreateScope();
        return await Repository(scope).TryAddAsync(insights, default);
    }

    private async Task<WeeklyReview> ReviewAsync(User user)
    {
        var review = WeeklyReview.Create(user.Id, WeekEnd, "Europe/Rome", Now.AddHours(-12), new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 42.50m, 100m, 57.50m, [new WeeklyExpenseCategory("Food", 42.50m)])]),
            new WeeklyGymSummary(1, 3600, 10, 12, [new WeeklyWorkout(WeekEnd.AddDays(-5), "Upper", "Base", 3600, 10, 12)]),
            new WeeklyNutritionSummary(1, 2, 1, 0, 650.5m, 30m, 80m, 20m, [new WeeklyNutritionDay(WeekEnd.AddDays(-6), 2, 1, 650.5m, 30m, 80m, 20m)])));

        await using var scope = fixture.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IWeeklyReviewRepository>().TryAddAsync(review, default));

        return review;
    }

    // The whole stored row, as text: proves the deterministic review is byte-for-byte unchanged.
    private async Task<string> ReviewRowAsync(Guid reviewId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database.SqlQuery<string>($"SELECT row_to_json(r)::text AS \"Value\" FROM weekly_reviews AS r WHERE id = {reviewId}").SingleAsync();
    }

    private async Task<int> CountAsync(Guid reviewId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM weekly_review_insights WHERE review_id = {reviewId}").SingleAsync();
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-60));
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IWeeklyReviewInsightsRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWeeklyReviewInsightsRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
