using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class AccountReconciliationConfiguration : IEntityTypeConfiguration<AccountReconciliation>
{
    public const string AccountForeignKeyName = "FK_account_reconciliations_accounts_account_id_user_id";
    public const string RequestIndexName = "ux_account_reconciliations_account_user_request";
    public void Configure(EntityTypeBuilder<AccountReconciliation> builder)
    {
        builder.ToTable("account_reconciliations");
        builder.HasKey(r => r.Id);
        builder.HasAlternateKey(r => new { r.Id, r.AccountId, r.UserId });
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.AccountId).HasColumnName("account_id");
        builder.Property(r => r.RequestId).HasColumnName("request_id");
        builder.Property(r => r.PreviousBalance).HasColumnName("previous_balance").HasColumnType("numeric");
        builder.Property(r => r.ObservedBalance).HasColumnName("observed_balance").HasPrecision(19, 4);
        builder.Ignore(r => r.AdjustmentAmount);
        builder.Property(r => r.EffectiveAtUtc).HasColumnName("effective_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(r => r.Note).HasColumnName("note");
        builder.HasIndex(r => new { r.AccountId, r.UserId, r.RequestId }).IsUnique().HasDatabaseName(RequestIndexName);
        builder.HasOne<Account>().WithMany().HasForeignKey(r => new { r.AccountId, r.UserId })
            .HasPrincipalKey(a => new { a.Id, a.UserId }).HasConstraintName(AccountForeignKeyName).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
