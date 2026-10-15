using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// HYD-001 §9. No total column anywhere: daily totals are SUM(amount_ml) per diary date.
internal sealed class HydrationIntakeConfiguration : IEntityTypeConfiguration<HydrationIntake>
{
    // Serves one diary day of one user in diary order (newest first) and, with amount_ml included, the
    // per-day totals of a date range as an index-only scan.
    public const string DiaryIndexName = "ix_hydration_intakes_user_diary";

    public void Configure(EntityTypeBuilder<HydrationIntake> builder)
    {
        builder.ToTable("hydration_intakes", table =>
        {
            table.HasCheckConstraint("ck_hydration_intakes_amount",
                $"amount_ml BETWEEN {HydrationIntake.MinAmountMl} AND {HydrationIntake.MaxAmountMl}");
            table.HasCheckConstraint("ck_hydration_intakes_diary_date",
                "diary_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
            table.HasCheckConstraint("ck_hydration_intakes_diary_time",
                "extract(second FROM diary_time) = 0");
            // The recorded UTC offset, (diary_date + diary_time) - occurred_at_utc, is a real-world one.
            table.HasCheckConstraint("ck_hydration_intakes_utc_offset",
                $"(diary_date + diary_time) - (occurred_at_utc AT TIME ZONE 'UTC') BETWEEN interval '-{HydrationIntake.MaxUtcOffsetMinutes} minutes' AND interval '{HydrationIntake.MaxUtcOffsetMinutes} minutes'");
        });

        builder.HasKey(intake => intake.Id);

        builder.Property(intake => intake.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(intake => intake.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(intake => intake.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(intake => intake.AmountMl)
            .HasColumnName("amount_ml");

        builder.Property(intake => intake.DiaryDate)
            .HasColumnName("diary_date")
            .HasColumnType("date");

        builder.Property(intake => intake.DiaryTime)
            .HasColumnName("diary_time")
            .HasColumnType("time without time zone");

        builder.Property(intake => intake.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(intake => intake.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(intake => intake.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(intake => intake.UtcOffsetMinutes);

        builder.HasIndex(intake => new { intake.UserId, intake.DiaryDate, intake.DiaryTime, intake.Id })
            .IsDescending(false, false, true, true)
            .IncludeProperties(intake => intake.AmountMl)
            .HasDatabaseName(DiaryIndexName);
    }
}

// HYD-001 P-4: open-ended target versions, one per user and effective date.
internal sealed class HydrationTargetVersionConfiguration : IEntityTypeConfiguration<HydrationTargetVersion>
{
    public void Configure(EntityTypeBuilder<HydrationTargetVersion> builder)
    {
        builder.ToTable("hydration_target_versions", table =>
        {
            table.HasCheckConstraint("ck_hydration_target_versions_target",
                $"target_ml IS NULL OR target_ml BETWEEN {HydrationTargetVersion.MinTargetMl} AND {HydrationTargetVersion.MaxTargetMl}");
            table.HasCheckConstraint("ck_hydration_target_versions_effective_from",
                "effective_from BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
        });

        builder.HasKey(version => new { version.UserId, version.EffectiveFrom });

        builder.Property(version => version.UserId)
            .HasColumnName("user_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(version => version.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(version => version.EffectiveFrom)
            .HasColumnName("effective_from")
            .HasColumnType("date");

        builder.Property(version => version.TargetMl)
            .HasColumnName("target_ml");

        builder.Property(version => version.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
    }
}

// HYD-001 P-6: at most one row per user; no row means the default quick-add amounts.
internal sealed class HydrationSettingsConfiguration : IEntityTypeConfiguration<HydrationSettings>
{
    public void Configure(EntityTypeBuilder<HydrationSettings> builder)
    {
        var amount = $"BETWEEN {HydrationIntake.MinAmountMl} AND {HydrationIntake.MaxAmountMl}";

        builder.ToTable("hydration_settings", table =>
        {
            table.HasCheckConstraint("ck_hydration_settings_quick_add",
                $"quick_add_1_ml {amount} AND quick_add_2_ml {amount} AND quick_add_3_ml {amount}");
        });

        builder.HasKey(settings => settings.UserId);

        builder.Property(settings => settings.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(settings => settings.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(settings => settings.QuickAdd1Ml)
            .HasColumnName("quick_add_1_ml");

        builder.Property(settings => settings.QuickAdd2Ml)
            .HasColumnName("quick_add_2_ml");

        builder.Property(settings => settings.QuickAdd3Ml)
            .HasColumnName("quick_add_3_ml");

        builder.Property(settings => settings.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Ignore(settings => settings.QuickAddMl);
    }
}
