using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class MealEntryConfiguration : IEntityTypeConfiguration<MealEntry>
{
    // Serves one diary day of one user, already in journal order (newest first).
    public const string DiaryIndexName = "ix_meal_entries_user_diary";

    public void Configure(EntityTypeBuilder<MealEntry> builder)
    {
        builder.ToTable("meal_entries", table =>
        {
            table.HasCheckConstraint("ck_meal_entries_description",
                $"length(btrim(description)) > 0 AND char_length(description) <= {MealEntry.DescriptionMaxLength}");
            table.HasCheckConstraint("ck_meal_entries_meal_type",
                "meal_type IS NULL OR meal_type IN ('Breakfast', 'Lunch', 'Dinner', 'Snack', 'Other')");
            table.HasCheckConstraint("ck_meal_entries_diary_date",
                "diary_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
            table.HasCheckConstraint("ck_meal_entries_diary_time",
                "extract(second FROM diary_time) = 0");
            // The recorded UTC offset, (diary_date + diary_time) - occurred_at_utc, is a real-world one.
            table.HasCheckConstraint("ck_meal_entries_utc_offset",
                $"(diary_date + diary_time) - (occurred_at_utc AT TIME ZONE 'UTC') BETWEEN interval '-{MealEntry.MaxUtcOffsetMinutes} minutes' AND interval '{MealEntry.MaxUtcOffsetMinutes} minutes'");
        });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entry => entry.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(entry => entry.Description)
            .HasColumnName("description")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(entry => entry.MealType)
            .HasColumnName("meal_type")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(entry => entry.DiaryDate)
            .HasColumnName("diary_date")
            .HasColumnType("date");

        builder.Property(entry => entry.DiaryTime)
            .HasColumnName("diary_time")
            .HasColumnType("time without time zone");

        builder.Property(entry => entry.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(entry => entry.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(entry => entry.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(entry => entry.UtcOffsetMinutes);

        builder.HasIndex(entry => new { entry.UserId, entry.DiaryDate, entry.DiaryTime, entry.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName(DiaryIndexName);
    }
}
