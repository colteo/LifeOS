using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class OpeningBalanceConfiguration : IEntityTypeConfiguration<OpeningBalance>
{
    // At most one opening balance per account; OpeningBalanceRepository recognizes it by name.
    public const string AccountIndexName = "ux_opening_balances_account_id";

    // The account reference; OpeningBalanceRepository and AccountRepository recognize it by name.
    public const string AccountForeignKeyName = "FK_opening_balances_accounts_account_id_user_id";

    public void Configure(EntityTypeBuilder<OpeningBalance> builder)
    {
        builder.ToTable("opening_balances");

        builder.HasKey(openingBalance => openingBalance.Id);

        builder.Property(openingBalance => openingBalance.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(openingBalance => openingBalance.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(openingBalance => openingBalance.AccountId)
            .HasColumnName("account_id")
            .IsRequired();

        // Signed (ADR-007): no "amount > 0" check, unlike transactions. No currency column: the
        // account's currency is authoritative.
        builder.Property(openingBalance => openingBalance.Amount)
            .HasColumnName("amount")
            .HasPrecision(19, 4)
            .IsRequired();

        builder.Property(openingBalance => openingBalance.AsOfUtc)
            .HasColumnName("as_of_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(openingBalance => openingBalance.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(openingBalance => openingBalance.AccountId)
            .IsUnique()
            .HasDatabaseName(AccountIndexName);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(openingBalance => openingBalance.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ownership backstop: (account_id, user_id) → accounts(id, user_id), as for transactions.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(openingBalance => new { openingBalance.AccountId, openingBalance.UserId })
            .HasPrincipalKey(account => new { account.Id, account.UserId })
            .HasConstraintName(AccountForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
