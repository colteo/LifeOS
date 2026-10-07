using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure.WeeklyReviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

// AI-001: at most one insights row per weekly review (the primary key is the review id, which is also
// the conflict target of every insert). Owned through the review: deleting the review, or its user,
// deletes the insights. No user_id column: ownership is always checked through weekly_reviews.
internal sealed class WeeklyReviewInsightsConfiguration : IEntityTypeConfiguration<WeeklyReviewInsightsRecord>
{
    public const string TableName = "weekly_review_insights";

    public const string ReviewForeignKeyName = "FK_weekly_review_insights_weekly_reviews_review_id";

    public void Configure(EntityTypeBuilder<WeeklyReviewInsightsRecord> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_weekly_review_insights_output_version", "output_version >= 1");
            table.HasCheckConstraint("ck_weekly_review_insights_content", "jsonb_typeof(content) = 'object'");
        });

        builder.HasKey(insights => insights.ReviewId);

        builder.Property(insights => insights.ReviewId)
            .HasColumnName("review_id")
            .ValueGeneratedNever();

        builder.Property(insights => insights.OutputVersion)
            .HasColumnName("output_version")
            .IsRequired();

        builder.Property(insights => insights.Content)
            .HasColumnName("content")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(insights => insights.Provider)
            .HasColumnName("provider")
            .HasMaxLength(WeeklyReviewInsights.MaxIdentityLength)
            .IsRequired();

        builder.Property(insights => insights.Model)
            .HasColumnName("model")
            .HasMaxLength(WeeklyReviewInsights.MaxIdentityLength)
            .IsRequired();

        builder.Property(insights => insights.PromptVersion)
            .HasColumnName("prompt_version")
            .HasMaxLength(WeeklyReviewInsights.MaxIdentityLength)
            .IsRequired();

        builder.Property(insights => insights.GeneratedAtUtc)
            .HasColumnName("generated_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasOne<WeeklyReviewRecord>()
            .WithOne()
            .HasForeignKey<WeeklyReviewInsightsRecord>(insights => insights.ReviewId)
            .HasConstraintName(ReviewForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
