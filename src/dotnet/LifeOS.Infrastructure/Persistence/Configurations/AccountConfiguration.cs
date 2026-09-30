using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts");

        builder.HasKey(account => account.Id);

        builder.Property(account => account.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(account => account.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // Target of the composite (account_id, user_id) foreign keys in transactions:
        // a transaction can only reference an account of the same user.
        builder.HasAlternateKey(account => new { account.Id, account.UserId });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(account => account.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(account => account.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(account => account.AccountType)
            .HasColumnName("account_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(account => account.Currency)
            .HasColumnName("currency")
            .HasColumnType("character(3)")
            .IsRequired();

        builder.Property(account => account.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
