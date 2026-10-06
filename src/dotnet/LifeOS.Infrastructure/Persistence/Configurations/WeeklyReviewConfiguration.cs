using LifeOS.Domain.Users;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure.WeeklyReviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class WeeklyReviewConfiguration : IEntityTypeConfiguration<WeeklyReviewRecord>
{
    public const string TableName = "weekly_reviews";

    // One review per user and local week (AUTO-002 W-6): the conflict target of every insert, and the
    // index of the newest-first list.
    public const string UserWeekIndexName = "ux_weekly_reviews_user_week_end";

    public const string UserForeignKeyName = "FK_weekly_reviews_users_user_id";

    public void Configure(EntityTypeBuilder<WeeklyReviewRecord> builder)
    {
        builder.ToTable(TableName, table =>
        {
            // A local Monday–Sunday week (ISODOW 7 = Sunday).
            table.HasCheckConstraint("ck_weekly_reviews_week",
                "week_end_date = week_start_date + 6 AND EXTRACT(ISODOW FROM week_end_date) = 7");
            table.HasCheckConstraint("ck_weekly_reviews_data_version", "data_version >= 1");
            table.HasCheckConstraint("ck_weekly_reviews_snapshot", "jsonb_typeof(snapshot) = 'object'");
        });

        builder.HasKey(review => review.Id);

        builder.Property(review => review.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(review => review.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(review => review.WeekStartDate)
            .HasColumnName("week_start_date")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(review => review.WeekEndDate)
            .HasColumnName("week_end_date")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(review => review.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasMaxLength(User.MaxTimeZoneIdLength)
            .IsRequired();

        builder.Property(review => review.GeneratedAtUtc)
            .HasColumnName("generated_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(review => review.DataVersion)
            .HasColumnName("data_version")
            .IsRequired();

        builder.Property(review => review.Snapshot)
            .HasColumnName("snapshot")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasIndex(review => new { review.UserId, review.WeekEndDate })
            .IsUnique()
            .HasDatabaseName(UserWeekIndexName);

        // User-owned data: deleting the user deletes their reviews (ADR-006).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(review => review.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WeeklyReviewSettingsConfiguration : IEntityTypeConfiguration<WeeklyReviewSettings>
{
    public const string TableName = "weekly_review_settings";

    public const string UserForeignKeyName = "FK_weekly_review_settings_users_user_id";

    public void Configure(EntityTypeBuilder<WeeklyReviewSettings> builder)
    {
        builder.ToTable(TableName);

        // At most one row per user; no row = the default (enabled).
        builder.HasKey(settings => settings.UserId);

        builder.Property(settings => settings.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();

        builder.Property(settings => settings.Enabled)
            .HasColumnName("enabled")
            .IsRequired();

        builder.Property(settings => settings.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<WeeklyReviewSettings>(settings => settings.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
