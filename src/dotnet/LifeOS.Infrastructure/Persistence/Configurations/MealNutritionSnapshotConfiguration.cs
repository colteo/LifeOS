using LifeOS.Domain.Nutrition;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class MealNutritionSnapshotConfiguration : IEntityTypeConfiguration<MealNutritionSnapshot>
{
    public const string TableName = "meal_nutrition_snapshots";

    // At most one snapshot per meal: the conflict target of every snapshot insert.
    public const string MealIndexName = "ux_meal_nutrition_snapshots_meal_entry";

    public void Configure(EntityTypeBuilder<MealNutritionSnapshot> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_meal_nutrition_snapshots_values",
                $"calories_kcal BETWEEN 0 AND {NutritionValues.MaxCaloriesKcal:0} " +
                $"AND protein_grams BETWEEN 0 AND {NutritionValues.MaxMacroGrams:0} " +
                $"AND carbs_grams BETWEEN 0 AND {NutritionValues.MaxMacroGrams:0} " +
                $"AND fat_grams BETWEEN 0 AND {NutritionValues.MaxMacroGrams:0}");
            table.HasCheckConstraint("ck_meal_nutrition_snapshots_source",
                "source IN ('AiConfirmed', 'AiRequested', 'AiAutoClosed', 'UserAdjusted')");
        });

        builder.HasKey(snapshot => snapshot.Id);

        builder.Property(snapshot => snapshot.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(snapshot => snapshot.MealEntryId)
            .HasColumnName("meal_entry_id")
            .IsRequired();

        // Part of the meal: deleting the meal deletes its nutrition.
        builder.HasOne<MealEntry>()
            .WithOne()
            .HasForeignKey<MealNutritionSnapshot>(snapshot => snapshot.MealEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(snapshot => snapshot.MealEntryId)
            .IsUnique()
            .HasDatabaseName(MealIndexName);

        builder.Property(snapshot => snapshot.CaloriesKcal)
            .HasColumnName("calories_kcal")
            .HasPrecision(6, NutritionValues.Decimals);

        builder.Property(snapshot => snapshot.ProteinGrams)
            .HasColumnName("protein_grams")
            .HasPrecision(5, NutritionValues.Decimals);

        builder.Property(snapshot => snapshot.CarbsGrams)
            .HasColumnName("carbs_grams")
            .HasPrecision(5, NutritionValues.Decimals);

        builder.Property(snapshot => snapshot.FatGrams)
            .HasColumnName("fat_grams")
            .HasPrecision(5, NutritionValues.Decimals);

        builder.Property(snapshot => snapshot.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(snapshot => snapshot.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(snapshot => snapshot.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(snapshot => snapshot.Values);
    }
}
