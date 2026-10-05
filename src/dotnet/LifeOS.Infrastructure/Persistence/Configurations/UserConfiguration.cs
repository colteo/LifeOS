using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(user => user.DisplayName)
            .HasColumnName("display_name")
            .HasColumnType("text");

        // Not unique: email is informational and never an identity key.
        builder.Property(user => user.Email)
            .HasColumnName("email")
            .HasColumnType("text");

        builder.Property(user => user.OnboardingStatus)
            .HasColumnName("onboarding_status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(user => user.DefaultCurrency)
            .HasColumnName("default_currency")
            .HasColumnType("character(3)");

        builder.Property(user => user.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasMaxLength(User.MaxTimeZoneIdLength);

        builder.HasIndex(user => user.TimeZoneId)
            .HasDatabaseName("ix_users_time_zone_id");

        builder.Property(user => user.StarterCategoriesInitializedAtUtc)
            .HasColumnName("starter_categories_initialized_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(user => user.OnboardingCompletedAtUtc)
            .HasColumnName("onboarding_completed_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(user => user.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
