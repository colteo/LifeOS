using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class NutritionTargetConfiguration : IEntityTypeConfiguration<NutritionTarget>
{
    public const string TableName = "nutrition_targets";

    // At most one state per user and effective date: the conflict target of every save. It also serves
    // the effective-target lookup (user_id = ? AND effective_from <= ? ORDER BY effective_from DESC LIMIT 1).
    public const string UserEffectiveFromIndexName = "ux_nutrition_targets_user_effective_from";

    public void Configure(EntityTypeBuilder<NutritionTarget> builder)
    {
        builder.ToTable(TableName, table =>
        {
            // Null: not targeted. Present: more than zero and a plausible daily amount. All null is the
            // explicit "no targets" state.
            table.HasCheckConstraint("ck_nutrition_targets_values",
                $"(calories_kcal IS NULL OR (calories_kcal > 0 AND calories_kcal <= {NutritionTargetValues.MaxCaloriesKcal:0})) " +
                $"AND (protein_grams IS NULL OR (protein_grams > 0 AND protein_grams <= {NutritionTargetValues.MaxMacroGrams:0})) " +
                $"AND (carbs_grams IS NULL OR (carbs_grams > 0 AND carbs_grams <= {NutritionTargetValues.MaxMacroGrams:0})) " +
                $"AND (fat_grams IS NULL OR (fat_grams > 0 AND fat_grams <= {NutritionTargetValues.MaxMacroGrams:0}))");
            table.HasCheckConstraint("ck_nutrition_targets_source", "source IN ('Manual')");
            table.HasCheckConstraint("ck_nutrition_targets_effective_from",
                "effective_from BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
        });

        builder.HasKey(target => target.Id);

        builder.Property(target => target.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(target => target.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(target => target.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(target => target.EffectiveFrom)
            .HasColumnName("effective_from")
            .HasColumnType("date");

        builder.HasIndex(target => new { target.UserId, target.EffectiveFrom })
            .IsUnique()
            .HasDatabaseName(UserEffectiveFromIndexName);

        builder.Property(target => target.CaloriesKcal)
            .HasColumnName("calories_kcal")
            .HasPrecision(6, NutritionTargetValues.Decimals);

        builder.Property(target => target.ProteinGrams)
            .HasColumnName("protein_grams")
            .HasPrecision(5, NutritionTargetValues.Decimals);

        builder.Property(target => target.CarbsGrams)
            .HasColumnName("carbs_grams")
            .HasPrecision(5, NutritionTargetValues.Decimals);

        builder.Property(target => target.FatGrams)
            .HasColumnName("fat_grams")
            .HasPrecision(5, NutritionTargetValues.Decimals);

        // By name, with room for later origins; the check constraint lists the ones that exist.
        builder.Property(target => target.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(target => target.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(target => target.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(target => target.HasTargets);
        builder.Ignore(target => target.Values);
    }
}
