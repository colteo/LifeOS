using LifeOS.Domain.Nutrition;

namespace LifeOS.UnitTests.Nutrition;

public class MealEntryTests
{
    private static readonly Guid User = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 11, 15, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 3);

    private static MealEntry Create(string description = "Pasta", MealType? type = MealType.Lunch, TimeOnly? time = null, int offset = 120) =>
        MealEntry.Create(User, description, type, Day, time ?? new TimeOnly(13, 10), offset, Now);

    [Fact]
    public void Create_StoresTheDiaryDayAndTime_AndDerivesTheUtcInstant()
    {
        var entry = Create();

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(7, entry.Id.Version);
        Assert.Equal((User, "Pasta", MealType.Lunch), (entry.UserId, entry.Description, entry.MealType));
        Assert.Equal((Day, new TimeOnly(13, 10)), (entry.DiaryDate, entry.DiaryTime));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 11, 10, 0, TimeSpan.Zero), entry.OccurredAtUtc);
        Assert.Equal(TimeSpan.Zero, entry.OccurredAtUtc.Offset);
        Assert.Equal(120, entry.UtcOffsetMinutes);
        Assert.Equal((Now, Now), (entry.CreatedAtUtc, entry.UpdatedAtUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Description_IsRequired_AndNotWhitespaceOnly(string description)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => Create(description));

        Assert.Equal("description", exception.ParamName);
    }

    [Fact]
    public void Description_IsTrimmed_AndKeepsInternalTextAndLineBreaks()
    {
        var entry = Create("  200 g grilled chicken,\nbasmati rice,  zucchini\n\nolive oil \n ");

        Assert.Equal("200 g grilled chicken,\nbasmati rice,  zucchini\n\nolive oil", entry.Description);
    }

    [Fact]
    public void Description_IsAtMost2000Characters_AfterTrimming()
    {
        Assert.Equal(2000, Create(new string('a', 2000)).Description.Length);
        Assert.Equal(2000, Create("  " + new string('a', 2000) + "  ").Description.Length);

        var exception = Assert.ThrowsAny<ArgumentException>(() => Create(new string('a', 2001)));
        Assert.Equal("description", exception.ParamName);
    }

    [Theory]
    [InlineData(MealType.Breakfast)]
    [InlineData(MealType.Lunch)]
    [InlineData(MealType.Dinner)]
    [InlineData(MealType.Snack)]
    [InlineData(MealType.Other)]
    public void MealType_AcceptsEveryDefinedValue(MealType type) => Assert.Equal(type, Create(type: type).MealType);

    [Fact]
    public void MealType_IsOptional()
    {
        Assert.Null(Create(type: null).MealType);
    }

    [Fact]
    public void MealType_RejectsUndefinedValues()
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => Create(type: (MealType)42));

        Assert.Equal("mealType", exception.ParamName);
    }

    [Fact]
    public void MealTypes_AreExactlyTheFiveLabels()
    {
        Assert.Equal(["Breakfast", "Lunch", "Dinner", "Snack", "Other"], Enum.GetNames<MealType>());
    }

    [Fact]
    public void Time_MustBeAWholeMinute()
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => Create(time: new TimeOnly(13, 10, 5)));

        Assert.Equal("time", exception.ParamName);
    }

    [Theory]
    [InlineData(-841)]
    [InlineData(841)]
    public void UtcOffset_IsARealWorldOne(int offset)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => Create(offset: offset));

        Assert.Equal("utcOffsetMinutes", exception.ParamName);
    }

    [Fact]
    public void LateEveningMeal_StaysOnItsDiaryDay_WhateverItsUtcDay()
    {
        // 23:30 in UTC+02:00 is 21:30 UTC the same day; 00:30 in UTC+02:00 is 22:30 UTC the day before.
        var late = MealEntry.Create(User, "Tisana", null, Day, new TimeOnly(23, 30), 120, Now);
        var early = MealEntry.Create(User, "Latte", null, Day, new TimeOnly(0, 30), 120, Now);

        Assert.Equal(Day, late.DiaryDate);
        Assert.Equal(Day, early.DiaryDate);
        Assert.Equal(new DateOnly(2026, 10, 2), DateOnly.FromDateTime(early.OccurredAtUtc.UtcDateTime));
    }

    [Theory]
    [InlineData("0001-01-01")]
    [InlineData("9999-12-31")]
    public void DiaryDate_MustBeFinite(string date)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            MealEntry.Create(User, "Pasta", null, DateOnly.Parse(date), new TimeOnly(12, 0), 0, Now));

        Assert.Equal("diaryDate", exception.ParamName);
    }

    [Fact]
    public void Owner_IsRequired()
    {
        Assert.ThrowsAny<ArgumentException>(() => MealEntry.Create(Guid.Empty, "Pasta", null, Day, new TimeOnly(12, 0), 0, Now));
    }

    [Fact]
    public void Update_ChangesTextTypeAndTime_KeepsDayOffsetAndCreation()
    {
        var entry = Create();
        var later = Now.AddHours(3);

        entry.Update("  Risotto\nai funghi ", null, new TimeOnly(20, 15), later);

        Assert.Equal(("Risotto\nai funghi", (MealType?)null), (entry.Description, entry.MealType));
        Assert.Equal((Day, new TimeOnly(20, 15)), (entry.DiaryDate, entry.DiaryTime));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 18, 15, 0, TimeSpan.Zero), entry.OccurredAtUtc);
        Assert.Equal(120, entry.UtcOffsetMinutes);
        Assert.Equal((Now, later), (entry.CreatedAtUtc, entry.UpdatedAtUtc));
    }

    [Fact]
    public void Update_EnforcesTheSameInvariants_AndChangesNothingWhenInvalid()
    {
        var entry = Create();

        Assert.Equal("description", Assert.ThrowsAny<ArgumentException>(() => entry.Update(" ", null, new TimeOnly(9, 0), Now)).ParamName);
        Assert.Equal("description", Assert.ThrowsAny<ArgumentException>(() => entry.Update(new string('x', 2001), null, new TimeOnly(9, 0), Now)).ParamName);
        Assert.Equal("mealType", Assert.ThrowsAny<ArgumentException>(() => entry.Update("Ok", (MealType)9, new TimeOnly(9, 0), Now)).ParamName);
        Assert.Equal("time", Assert.ThrowsAny<ArgumentException>(() => entry.Update("Ok", null, new TimeOnly(9, 0, 1), Now)).ParamName);

        Assert.Equal(("Pasta", MealType.Lunch, new TimeOnly(13, 10)), (entry.Description, entry.MealType, entry.DiaryTime));
    }
}
