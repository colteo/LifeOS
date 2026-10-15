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
