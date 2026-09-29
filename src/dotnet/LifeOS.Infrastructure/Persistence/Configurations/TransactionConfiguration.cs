using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    // Safety backstops for the Domain rules; they complement, not replace, them.
    private const string ShapeConstraint =
        "(transaction_type IN ('Income', 'Expense')"
        + " AND account_id IS NOT NULL AND category_id IS NOT NULL"
        + " AND source_account_id IS NULL AND destination_account_id IS NULL)"
        + " OR (transaction_type = 'Transfer'"
        + " AND account_id IS NULL AND category_id IS NULL"
        + " AND source_account_id IS NOT NULL AND destination_account_id IS NOT NULL"
        + " AND source_account_id <> destination_account_id)";

    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions", table =>
        {
            table.HasCheckConstraint("ck_transactions_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_transactions_shape", ShapeConstraint);
        });

        builder.HasKey(transaction => transaction.Id);

        builder.Property(transaction => transaction.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(transaction => transaction.TransactionType)
            .HasColumnName("transaction_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        // numeric(19,4) matches Transaction.MaxAmount and Transaction.MaxDecimalPlaces.
        builder.Property(transaction => transaction.Amount)
            .HasColumnName("amount")
            .HasPrecision(19, 4)
            .IsRequired();

        builder.Property(transaction => transaction.Currency)
            .HasColumnName("currency")
            .HasColumnType("character(3)")
            .IsRequired();

        builder.Property(transaction => transaction.AccountId)
            .HasColumnName("account_id");

        builder.Property(transaction => transaction.SourceAccountId)
            .HasColumnName("source_account_id");

        builder.Property(transaction => transaction.DestinationAccountId)
            .HasColumnName("destination_account_id");

        builder.Property(transaction => transaction.CategoryId)
            .HasColumnName("category_id");

        builder.Property(transaction => transaction.Note)
            .HasColumnName("note")
            .HasColumnType("text");

        builder.Property(transaction => transaction.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(transaction => transaction.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => transaction.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => transaction.SourceAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => transaction.DestinationAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(transaction => transaction.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
