using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class RecurringRuleConfiguration : IEntityTypeConfiguration<RecurringTransactionRule>
{
    public const string AccountForeignKey = "fk_recurring_rules_account_owner";
    public const string CategoryForeignKey = "fk_recurring_rules_category_owner";
    public void Configure(EntityTypeBuilder<RecurringTransactionRule> b)
    {
        b.ToTable("recurring_transaction_rules", t =>
        {
            t.HasCheckConstraint("ck_recurring_rules_type", "transaction_type IN ('Income', 'Expense')");
            t.HasCheckConstraint("ck_recurring_rules_amount", "amount > 0");
            t.HasCheckConstraint("ck_recurring_rules_day", "day_of_month BETWEEN 1 AND 31");
            t.HasCheckConstraint("ck_recurring_rules_start", "start_year BETWEEN 1 AND 9998 AND start_month BETWEEN 1 AND 12");
            t.HasCheckConstraint("ck_recurring_rules_name", "length(btrim(name)) > 0");
        });
        b.HasKey(r => r.Id); b.HasAlternateKey(r => new { r.Id, r.UserId });
        b.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(r => r.UserId).HasColumnName("user_id");
        b.Property(r => r.Name).HasColumnName("name").IsRequired();
        b.Property(r => r.TransactionType).HasColumnName("transaction_type").HasConversion<string>().HasMaxLength(32);
        b.Property(r => r.AccountId).HasColumnName("account_id");
        b.Property(r => r.CategoryId).HasColumnName("category_id");
        b.Property(r => r.Amount).HasColumnName("amount").HasPrecision(19, 4);
        b.Property(r => r.DayOfMonth).HasColumnName("day_of_month");
        b.Property(r => r.StartYear).HasColumnName("start_year");
        b.Property(r => r.StartMonth).HasColumnName("start_month");
        b.Property(r => r.Note).HasColumnName("note");
        b.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(r => r.UpdatedAtUtc).HasColumnName("updated_at_utc");
        b.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(r => new { r.AccountId, r.UserId })
            .HasPrincipalKey(a => new { a.Id, a.UserId }).HasConstraintName(AccountForeignKey).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(r => new { r.CategoryId, r.UserId })
            .HasPrincipalKey(c => new { c.Id, c.UserId }).HasConstraintName(CategoryForeignKey).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RecurringOccurrenceConfiguration : IEntityTypeConfiguration<RecurringOccurrenceState>
{
    public const string MonthIndex = "ux_recurring_occurrences_rule_month";
    public void Configure(EntityTypeBuilder<RecurringOccurrenceState> b)
    {
        b.ToTable("recurring_transaction_occurrences", t =>
        {
            t.HasCheckConstraint("ck_recurring_occurrences_month", "year BETWEEN 1 AND 9998 AND month BETWEEN 1 AND 12");
            t.HasCheckConstraint("ck_recurring_occurrences_shape", "(status = 'Confirmed' AND transaction_id IS NOT NULL) OR (status = 'Skipped' AND transaction_id IS NULL)");
            t.HasCheckConstraint("ck_recurring_occurrences_date", "EXTRACT(YEAR FROM scheduled_date) = year AND EXTRACT(MONTH FROM scheduled_date) = month");
        });
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(s => s.UserId).HasColumnName("user_id");
        b.Property(s => s.RecurringRuleId).HasColumnName("recurring_rule_id");
        b.Property(s => s.Year).HasColumnName("year");
        b.Property(s => s.Month).HasColumnName("month");
        b.Property(s => s.ScheduledDate).HasColumnName("scheduled_date");
        b.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.TransactionId).HasColumnName("transaction_id");
        b.Property(s => s.CreatedAtUtc).HasColumnName("created_at_utc");
        b.HasIndex(s => new { s.RecurringRuleId, s.Year, s.Month }).IsUnique().HasDatabaseName(MonthIndex);
        b.HasOne<RecurringTransactionRule>().WithMany().HasForeignKey(s => new { s.RecurringRuleId, s.UserId })
            .HasPrincipalKey(r => new { r.Id, r.UserId }).OnDelete(DeleteBehavior.Cascade);
        // Deleting either planning data or the actual transaction removes only the state.
        b.HasOne<Transaction>().WithMany().HasForeignKey(s => new { s.TransactionId, s.UserId })
            .HasPrincipalKey(t => new { t.Id, t.UserId }).OnDelete(DeleteBehavior.Cascade);
    }
}
