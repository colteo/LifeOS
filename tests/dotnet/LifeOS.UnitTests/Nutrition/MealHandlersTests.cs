using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Nutrition;

public class MealHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly DateOnly Yesterday = new(2026, 10, 2);

    private readonly InMemoryMealEntryRepository _repository = new();
    private readonly FixedTimeProvider _clock = new(Now);

    private Task<MealResult> CreateAsync(Guid user, string description, TimeOnly time, MealType? type = null, DateOnly? day = null, int offset = 120) =>
        new CreateMealHandler(_repository, _clock).HandleAsync(user, new CreateMealCommand(description, type, day ?? Today, time, offset), CancellationToken.None);

    private async Task<IReadOnlyList<MealEntrySummary>> DayAsync(Guid user, DateOnly day)
    {
        var result = await new GetMealsForDateHandler(_repository).HandleAsync(user, day, CancellationToken.None);
        Assert.Equal(MealResultStatus.Ok, result.Status);
        return result.Meals;
    }

    private async Task<Guid> IdAsync(Guid user, string description, TimeOnly time, MealType? type = null, DateOnly? day = null)
    {
        var result = await CreateAsync(user, description, time, type, day);
        Assert.Equal(MealResultStatus.Ok, result.Status);
        return result.Meal!.Id;
    }

    [Fact]
    public async Task Create_PersistsAndReturnsTheMeal()
    {
        var result = await CreateAsync(TestUsers.A, "  Yogurt greco, banana e caffè ", new TimeOnly(8, 15), MealType.Breakfast);

        Assert.Equal(MealResultStatus.Ok, result.Status);
        var meal = result.Meal!;
        Assert.Equal(("Yogurt greco, banana e caffè", (MealType?)MealType.Breakfast, Today, new TimeOnly(8, 15)),
            (meal.Description, meal.MealType, meal.DiaryDate, meal.DiaryTime));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 6, 15, 0, TimeSpan.Zero), meal.OccurredAtUtc);
        Assert.Equal((Now, Now), (meal.CreatedAtUtc, meal.UpdatedAtUtc));
        Assert.Equal(TestUsers.A, Assert.Single(_repository.Entries).UserId);
    }

    [Fact]
    public async Task Create_WithoutMealType_IsAllowed()
    {
        var result = await CreateAsync(TestUsers.A, "Caffè", new TimeOnly(10, 30));

        Assert.Equal(MealResultStatus.Ok, result.Status);
        Assert.Null(result.Meal!.MealType);
        Assert.Null(Assert.Single(await DayAsync(TestUsers.A, Today)).MealType);
    }

    [Theory]
    [InlineData("", "description")]
    [InlineData("   ", "description")]
    public async Task Create_InvalidInput_IsInvalid_AndStoresNothing(string description, string field)
    {
        var result = await CreateAsync(TestUsers.A, description, new TimeOnly(12, 0));

        Assert.Equal((MealResultStatus.Invalid, field), (result.Status, result.Field));
        Assert.Empty(_repository.Entries);
    }

    [Fact]
    public async Task Create_InvalidTypeTimeOrOffset_IsInvalid()
    {
        Assert.Equal("mealType", (await CreateAsync(TestUsers.A, "x", new TimeOnly(12, 0), (MealType)7)).Field);
        Assert.Equal("time", (await CreateAsync(TestUsers.A, "x", new TimeOnly(12, 0, 30))).Field);
        Assert.Equal("utcOffsetMinutes", (await CreateAsync(TestUsers.A, "x", new TimeOnly(12, 0), offset: 900)).Field);
        Assert.Empty(_repository.Entries);
    }

    [Fact]
    public async Task DailyQuery_IsNewestFirst_ByTimeNotByMealTypeOrInsertion()
    {
        await IdAsync(TestUsers.A, "Pranzo", new TimeOnly(13, 10), MealType.Lunch);
        await IdAsync(TestUsers.A, "Colazione", new TimeOnly(8, 15), MealType.Breakfast);
        await IdAsync(TestUsers.A, "Cena", new TimeOnly(20, 15), MealType.Dinner);
        await IdAsync(TestUsers.A, "Caffè", new TimeOnly(10, 30));

        var meals = await DayAsync(TestUsers.A, Today);

        Assert.Equal(["Cena", "Pranzo", "Caffè", "Colazione"], meals.Select(meal => meal.Description));
    }

    [Fact]
    public async Task DailyQuery_SameTime_IsOrderedByIdDescending()
    {
        var first = await IdAsync(TestUsers.A, "First", new TimeOnly(12, 0));
        var second = await IdAsync(TestUsers.A, "Second", new TimeOnly(12, 0));
        var third = await IdAsync(TestUsers.A, "Third", new TimeOnly(12, 0));

        var meals = await DayAsync(TestUsers.A, Today);

        Assert.Equal(new[] { first, second, third }.OrderDescending(), meals.Select(meal => meal.Id));
    }

    [Fact]
    public async Task DailyQuery_ReturnsExactlyTheSelectedDiaryDay()
    {
        await IdAsync(TestUsers.A, "Yesterday late", new TimeOnly(23, 59), day: Yesterday);
        await IdAsync(TestUsers.A, "Today early", new TimeOnly(0, 0));
        await IdAsync(TestUsers.A, "Tomorrow", new TimeOnly(0, 0), day: Today.AddDays(1));

        Assert.Equal(["Yesterday late"], (await DayAsync(TestUsers.A, Yesterday)).Select(meal => meal.Description));
        Assert.Equal(["Today early"], (await DayAsync(TestUsers.A, Today)).Select(meal => meal.Description));
        Assert.Empty(await DayAsync(TestUsers.A, new DateOnly(2026, 9, 30)));
    }

    [Fact]
    public async Task DiaryDay_IsStable_RegardlessOfTheOffsetItWasRecordedWith()
    {
        // 23:30 recorded in UTC+02:00 and 00:30 recorded in UTC-05:00: their UTC days differ from the
        // diary days, which alone decide where they appear.
        var late = await CreateAsync(TestUsers.A, "Tisana", new TimeOnly(23, 30), day: Yesterday, offset: 120);
        var travel = await CreateAsync(TestUsers.A, "Snack in volo", new TimeOnly(0, 30), offset: -300);

        Assert.Equal(new DateTimeOffset(2026, 10, 2, 21, 30, 0, TimeSpan.Zero), late.Meal!.OccurredAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 5, 30, 0, TimeSpan.Zero), travel.Meal!.OccurredAtUtc);
        Assert.Equal(["Tisana"], (await DayAsync(TestUsers.A, Yesterday)).Select(meal => meal.Description));
        Assert.Equal(["Snack in volo"], (await DayAsync(TestUsers.A, Today)).Select(meal => meal.Description));
    }

    [Fact]
    public async Task DailyQuery_RejectsInfiniteDates()
    {
        var result = await new GetMealsForDateHandler(_repository).HandleAsync(TestUsers.A, DateOnly.MinValue, CancellationToken.None);

        Assert.Equal(MealResultStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Update_ChangesTextTypeAndTime_StaysOnItsDay_AndReorders()
    {
        var breakfast = await IdAsync(TestUsers.A, "Colazione", new TimeOnly(8, 0), MealType.Breakfast);
        await IdAsync(TestUsers.A, "Pranzo", new TimeOnly(13, 0), MealType.Lunch);
        var later = new FixedTimeProvider(Now.AddMinutes(5));

        var result = await new UpdateMealHandler(_repository, _repository, later)
            .HandleAsync(TestUsers.A, breakfast, new UpdateMealCommand(" Brunch ", null, new TimeOnly(14, 0)), CancellationToken.None);

        Assert.Equal(MealResultStatus.Ok, result.Status);
        Assert.Equal(("Brunch", (MealType?)null, Today, new TimeOnly(14, 0)), (result.Meal!.Description, result.Meal.MealType, result.Meal.DiaryDate, result.Meal.DiaryTime));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), result.Meal.OccurredAtUtc);
        Assert.Equal((Now, Now.AddMinutes(5)), (result.Meal.CreatedAtUtc, result.Meal.UpdatedAtUtc));
        Assert.Equal(["Brunch", "Pranzo"], (await DayAsync(TestUsers.A, Today)).Select(meal => meal.Description));
    }

    [Fact]
    public async Task Update_Invalid_ChangesNothing()
    {
        var id = await IdAsync(TestUsers.A, "Pasta", new TimeOnly(13, 0), MealType.Lunch);

        var result = await new UpdateMealHandler(_repository, _repository, _clock)
            .HandleAsync(TestUsers.A, id, new UpdateMealCommand("  ", null, new TimeOnly(9, 0)), CancellationToken.None);

        Assert.Equal((MealResultStatus.Invalid, "description"), (result.Status, result.Field));
        var stored = Assert.Single(await DayAsync(TestUsers.A, Today));
        Assert.Equal(("Pasta", (MealType?)MealType.Lunch, new TimeOnly(13, 0)), (stored.Description, stored.MealType, stored.DiaryTime));
    }

    [Fact]
    public async Task Delete_RemovesTheMeal()
    {
        var id = await IdAsync(TestUsers.A, "Pasta", new TimeOnly(13, 0));

        Assert.Equal(MealResultStatus.Ok, await new DeleteMealHandler(_repository).HandleAsync(TestUsers.A, id, CancellationToken.None));
        Assert.Empty(await DayAsync(TestUsers.A, Today));
        Assert.Equal(MealResultStatus.NotFound, await new DeleteMealHandler(_repository).HandleAsync(TestUsers.A, id, CancellationToken.None));
    }

    [Fact]
    public async Task OtherUsers_NeverSeeEditOrDeleteTheMeal()
    {
        var id = await IdAsync(TestUsers.A, "Pasta", new TimeOnly(13, 0), MealType.Lunch);

        Assert.Empty(await DayAsync(TestUsers.B, Today));

        var update = await new UpdateMealHandler(_repository, _repository, _clock)
            .HandleAsync(TestUsers.B, id, new UpdateMealCommand("Hacked", null, new TimeOnly(9, 0)), CancellationToken.None);
        Assert.Equal(MealResultStatus.NotFound, update.Status);

        Assert.Equal(MealResultStatus.NotFound, await new DeleteMealHandler(_repository).HandleAsync(TestUsers.B, id, CancellationToken.None));

        var stored = Assert.Single(await DayAsync(TestUsers.A, Today));
        Assert.Equal(("Pasta", (MealType?)MealType.Lunch), (stored.Description, stored.MealType));
    }

    [Fact]
    public async Task MissingMeal_IsNotFound()
    {
        var update = await new UpdateMealHandler(_repository, _repository, _clock)
            .HandleAsync(TestUsers.A, Guid.CreateVersion7(), new UpdateMealCommand("x", null, new TimeOnly(9, 0)), CancellationToken.None);

        Assert.Equal(MealResultStatus.NotFound, update.Status);
        Assert.Equal(MealResultStatus.NotFound, await new DeleteMealHandler(_repository).HandleAsync(TestUsers.A, Guid.CreateVersion7(), CancellationToken.None));
    }
}
