using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    // The reference foreign keys, named explicitly: repositories recognize violations by these names.
    public const string AccountForeignKeyName = "FK_transactions_accounts_account_id_user_id";
    public const string SourceAccountForeignKeyName = "FK_transactions_accounts_source_account_id_user_id";
    public const string DestinationAccountForeignKeyName = "FK_transactions_accounts_destination_account_id_user_id";
    public const string CategoryForeignKeyName = "FK_transactions_categories_category_id_user_id";

    public static readonly IReadOnlyCollection<string> AccountForeignKeyNames =
        [AccountForeignKeyName, SourceAccountForeignKeyName, DestinationAccountForeignKeyName];

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

        builder.Property(transaction => transaction.UserId)
            .HasColumnName("user_id")
            .IsRequired();

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

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(transaction => transaction.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ownership backstop: every reference is a composite (…_id, user_id) foreign key to
        // (id, user_id), so a transaction can only reference accounts and categories of its own user.
        // PostgreSQL's default MATCH SIMPLE skips the check when the reference column is NULL,
        // which preserves the Income/Expense vs Transfer shapes.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.AccountId, transaction.UserId })
            .HasPrincipalKey(account => new { account.Id, account.UserId })
            .HasConstraintName(AccountForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.SourceAccountId, transaction.UserId })
            .HasPrincipalKey(account => new { account.Id, account.UserId })
            .HasConstraintName(SourceAccountForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.DestinationAccountId, transaction.UserId })
            .HasPrincipalKey(account => new { account.Id, account.UserId })
            .HasConstraintName(DestinationAccountForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.CategoryId, transaction.UserId })
            .HasPrincipalKey(category => new { category.Id, category.UserId })
            .HasConstraintName(CategoryForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
