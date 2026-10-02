using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class AccountBalanceAdjustmentConfiguration : IEntityTypeConfiguration<AccountBalanceAdjustment>
{
    public const string AccountForeignKeyName = "FK_account_balance_adjustments_accounts_account_id_user_id";
    public void Configure(EntityTypeBuilder<AccountBalanceAdjustment> builder)
    {
        builder.ToTable("account_balance_adjustments", table => table.HasCheckConstraint("ck_account_balance_adjustments_nonzero", "amount <> 0"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.Property(a => a.AccountId).HasColumnName("account_id");
        builder.Property(a => a.Amount).HasColumnName("amount").HasPrecision(19, 4);
        builder.Property(a => a.ObservedBalance).HasColumnName("observed_balance").HasPrecision(19, 4);
        builder.Property(a => a.EffectiveAtUtc).HasColumnName("effective_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(a => a.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(a => a.Note).HasColumnName("note");
        builder.HasIndex(a => new { a.UserId, a.AccountId, a.EffectiveAtUtc }).HasDatabaseName("ix_account_balance_adjustments_user_account_time");
        builder.HasOne<Account>().WithMany().HasForeignKey(a => new { a.AccountId, a.UserId })
            .HasPrincipalKey(a => new { a.Id, a.UserId }).HasConstraintName(AccountForeignKeyName).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AccountReconciliation>().WithMany().HasForeignKey(a => new { a.Id, a.AccountId, a.UserId })
            .HasPrincipalKey(r => new { r.Id, r.AccountId, r.UserId }).HasConstraintName("FK_adjustments_reconciliation_receipt").OnDelete(DeleteBehavior.Restrict);
    }
}
