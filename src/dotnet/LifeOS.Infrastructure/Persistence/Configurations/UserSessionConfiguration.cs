using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");

        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(session => session.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(session => session.FamilyId)
            .HasColumnName("family_id")
            .IsRequired();

        // Lowercase hex SHA-256; the raw refresh token is never stored.
        builder.Property(session => session.RefreshTokenHash)
            .HasColumnName("refresh_token_hash")
            .HasColumnType("character(64)")
            .IsRequired();

        builder.Property(session => session.ExpiresAtUtc)
            .HasColumnName("expires_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(session => session.RevokedAtUtc)
            .HasColumnName("revoked_at_utc")
            .HasColumnType("timestamp with time zone");

        // Forward link only; no foreign key, so cascading user deletion stays simple.
        builder.Property(session => session.ReplacedBySessionId)
            .HasColumnName("replaced_by_session_id");

        builder.Property(session => session.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Ignore(session => session.IsRotated);

        builder.HasIndex(session => session.RefreshTokenHash)
            .IsUnique()
            .HasDatabaseName("ux_user_sessions_refresh_token_hash");

        builder.HasIndex(session => session.FamilyId)
            .HasDatabaseName("ix_user_sessions_family_id");

        // A session has no meaning without its user.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
