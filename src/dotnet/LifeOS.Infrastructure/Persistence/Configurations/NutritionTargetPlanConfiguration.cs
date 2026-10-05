using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class NutritionTargetPlanConfiguration : IEntityTypeConfiguration<NutritionTargetPlan>
{
    public const string TableName = "nutrition_target_plans";
    public const string RulesTableName = "nutrition_target_plan_day_rules";

    // Serves a user's plan list (by start) and the plan covering a date.
    public const string UserStartsOnIndexName = "ix_nutrition_target_plans_user_starts_on";

    // Raised by the trg_nutrition_target_plans_no_overlap trigger (SQLSTATE 23P01, exclusion_violation)
    // when a plan would overlap another plan of the same user. See the AddNutritionTargetPlans migration.
    public const string NoOverlapConstraintName = "ex_nutrition_target_plans_no_overlap";

    public void Configure(EntityTypeBuilder<NutritionTargetPlan> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_nutrition_target_plans_period",
                "starts_on >= DATE '0001-01-02' AND ends_on <= DATE '9999-12-30' AND ends_on >= starts_on");
            table.HasCheckConstraint("ck_nutrition_target_plans_default", TargetValueChecks.Sql("default_"));
        });

        builder.HasKey(plan => plan.Id);

        builder.Property(plan => plan.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(plan => plan.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(plan => plan.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(plan => plan.StartsOn)
            .HasColumnName("starts_on")
            .HasColumnType("date");

        builder.Property(plan => plan.EndsOn)
            .HasColumnName("ends_on")
            .HasColumnType("date");

        builder.HasIndex(plan => new { plan.UserId, plan.StartsOn })
            .HasDatabaseName(UserStartsOnIndexName);

        TargetValueChecks.Map(builder.Property(plan => plan.DefaultCaloriesKcal), "default_calories_kcal", 6);
        TargetValueChecks.Map(builder.Property(plan => plan.DefaultProteinGrams), "default_protein_grams", 5);
        TargetValueChecks.Map(builder.Property(plan => plan.DefaultCarbsGrams), "default_carbs_grams", 5);
        TargetValueChecks.Map(builder.Property(plan => plan.DefaultFatGrams), "default_fat_grams", 5);

        builder.Property(plan => plan.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(plan => plan.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(plan => plan.DefaultTarget);

        // Exactly seven rows per plan (one per ISO weekday), always persisted: the weekly pattern is
        // explicit in the data and resolution needs no "missing row means Default" rule.
        builder.OwnsMany(plan => plan.Rules, rules =>
        {
            rules.ToTable(RulesTableName, table =>
            {
                table.HasCheckConstraint("ck_nutrition_target_plan_day_rules_weekday", "weekday BETWEEN 1 AND 7");
                table.HasCheckConstraint("ck_nutrition_target_plan_day_rules_mode", "mode IN ('Default', 'Custom', 'NoTarget')");
                // Custom carries at least one value; Default and NoTarget carry none.
                table.HasCheckConstraint("ck_nutrition_target_plan_day_rules_values",
                    TargetValueChecks.Sql("") + " AND ((mode = 'Custom') = " +
                    "(calories_kcal IS NOT NULL OR protein_grams IS NOT NULL OR carbs_grams IS NOT NULL OR fat_grams IS NOT NULL))");
            });

            rules.WithOwner().HasForeignKey("PlanId").HasConstraintName("FK_nutrition_target_plan_day_rules_plan_id");
            rules.Property<Guid>("PlanId").HasColumnName("plan_id");

            rules.Property(rule => rule.Weekday)
                .HasColumnName("weekday")
                .HasColumnType("smallint")
                .HasConversion(day => (short)NutritionTargetPlan.Iso(day), value => NutritionTargetPlan.FromIso(value));

            rules.HasKey("PlanId", nameof(NutritionTargetDayRule.Weekday));

            rules.Property(rule => rule.Mode)
                .HasColumnName("mode")
                .HasConversion<string>()
                .HasMaxLength(16);

            TargetValueChecks.Map(rules.Property(rule => rule.CaloriesKcal), "calories_kcal", 6);
            TargetValueChecks.Map(rules.Property(rule => rule.ProteinGrams), "protein_grams", 5);
            TargetValueChecks.Map(rules.Property(rule => rule.CarbsGrams), "carbs_grams", 5);
            TargetValueChecks.Map(rules.Property(rule => rule.FatGrams), "fat_grams", 5);

            rules.Ignore(rule => rule.Target);
        });

        builder.Navigation(plan => plan.Rules)
            .HasField("_rules")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class NutritionTargetOverrideConfiguration : IEntityTypeConfiguration<NutritionTargetOverride>
{
    public const string TableName = "nutrition_target_overrides";

    // At most one override per user and date: the conflict target of every override save.
    public const string UserDateIndexName = "ux_nutrition_target_overrides_user_date";

    public void Configure(EntityTypeBuilder<NutritionTargetOverride> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_nutrition_target_overrides_mode", "mode IN ('Custom', 'NoTarget')");
            table.HasCheckConstraint("ck_nutrition_target_overrides_values",
                TargetValueChecks.Sql("") + " AND ((mode = 'Custom') = " +
                "(calories_kcal IS NOT NULL OR protein_grams IS NOT NULL OR carbs_grams IS NOT NULL OR fat_grams IS NOT NULL))");
        });

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(item => item.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Part of its plan: deleting the plan deletes its overrides.
        builder.Property(item => item.PlanId)
            .HasColumnName("plan_id")
            .IsRequired();

        builder.HasOne<NutritionTargetPlan>()
            .WithMany()
            .HasForeignKey(item => item.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(item => item.Date)
            .HasColumnName("date")
            .HasColumnType("date");

        builder.HasIndex(item => new { item.UserId, item.Date })
            .IsUnique()
            .HasDatabaseName(UserDateIndexName);

        builder.Property(item => item.Mode)
            .HasColumnName("mode")
            .HasConversion<string>()
            .HasMaxLength(16);

        TargetValueChecks.Map(builder.Property(item => item.CaloriesKcal), "calories_kcal", 6);
        TargetValueChecks.Map(builder.Property(item => item.ProteinGrams), "protein_grams", 5);
        TargetValueChecks.Map(builder.Property(item => item.CarbsGrams), "carbs_grams", 5);
        TargetValueChecks.Map(builder.Property(item => item.FatGrams), "fat_grams", 5);

        builder.Property(item => item.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(item => item.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(item => item.Target);
    }
}

// The four nullable target columns: null (not targeted) or > 0 and within the daily bounds.
internal static class TargetValueChecks
{
    public static string Sql(string prefix) =>
        $"({prefix}calories_kcal IS NULL OR ({prefix}calories_kcal > 0 AND {prefix}calories_kcal <= {NutritionTargetValues.MaxCaloriesKcal:0})) " +
        $"AND ({prefix}protein_grams IS NULL OR ({prefix}protein_grams > 0 AND {prefix}protein_grams <= {NutritionTargetValues.MaxMacroGrams:0})) " +
        $"AND ({prefix}carbs_grams IS NULL OR ({prefix}carbs_grams > 0 AND {prefix}carbs_grams <= {NutritionTargetValues.MaxMacroGrams:0})) " +
        $"AND ({prefix}fat_grams IS NULL OR ({prefix}fat_grams > 0 AND {prefix}fat_grams <= {NutritionTargetValues.MaxMacroGrams:0}))";

    public static void Map(PropertyBuilder<decimal?> property, string column, int precision) =>
        property.HasColumnName(column).HasPrecision(precision, NutritionTargetValues.Decimals);
}
