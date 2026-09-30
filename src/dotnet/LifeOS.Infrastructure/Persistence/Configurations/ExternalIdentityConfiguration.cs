using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> builder)
    {
        builder.ToTable("external_identities");

        builder.HasKey(identity => identity.Id);

        builder.Property(identity => identity.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(identity => identity.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(identity => identity.Provider)
            .HasColumnName("provider")
            .HasMaxLength(ExternalIdentity.MaxProviderLength)
            .IsRequired();

        builder.Property(identity => identity.Subject)
            .HasColumnName("subject")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(identity => identity.EmailAtSignIn)
            .HasColumnName("email_at_sign_in")
            .HasColumnType("text");

        builder.Property(identity => identity.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(identity => identity.LastSignInAtUtc)
            .HasColumnName("last_sign_in_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // The identity key. Also the final backstop against concurrent first sign-ins.
        builder.HasIndex(identity => new { identity.Provider, identity.Subject })
            .IsUnique();

        // An identity has no meaning without its user.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
