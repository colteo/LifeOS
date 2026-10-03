using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class PlannedExpenseConfiguration : IEntityTypeConfiguration<PlannedExpense>
{
    public const string AccountForeignKey = "fk_planned_expenses_account_owner";
    public const string CategoryForeignKey = "fk_planned_expenses_category_owner";
    public void Configure(EntityTypeBuilder<PlannedExpense> b)
    {
        b.ToTable("planned_expenses", t =>
        {
            t.HasCheckConstraint("ck_planned_expenses_amount", "expected_amount > 0");
            t.HasCheckConstraint("ck_planned_expenses_name", "length(btrim(name)) > 0");
            t.HasCheckConstraint("ck_planned_expenses_date", "scheduled_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
        });
        b.HasKey(i => i.Id); b.HasAlternateKey(i => new { i.Id, i.UserId });
        b.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(i => i.UserId).HasColumnName("user_id");
        b.Property(i => i.Name).HasColumnName("name").IsRequired();
        b.Property(i => i.AccountId).HasColumnName("account_id");
        b.Property(i => i.CategoryId).HasColumnName("category_id");
        b.Property(i => i.ExpectedAmount).HasColumnName("expected_amount").HasPrecision(19, 4);
        b.Property(i => i.ScheduledDate).HasColumnName("scheduled_date");
        b.Property(i => i.Note).HasColumnName("note");
        b.Property(i => i.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(i => i.UpdatedAtUtc).HasColumnName("updated_at_utc");
        b.HasIndex(i => new { i.UserId, i.ScheduledDate });
        b.HasOne<User>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(i => new { i.AccountId, i.UserId }).HasPrincipalKey(a => new { a.Id, a.UserId })
            .HasConstraintName(AccountForeignKey).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(i => new { i.CategoryId, i.UserId }).HasPrincipalKey(c => new { c.Id, c.UserId })
            .HasConstraintName(CategoryForeignKey).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PlannedExpenseStateConfiguration : IEntityTypeConfiguration<PlannedExpenseState>
{
    public const string ItemIndex = "ux_planned_expense_states_item";
    public void Configure(EntityTypeBuilder<PlannedExpenseState> b)
    {
        b.ToTable("planned_expense_states", t => t.HasCheckConstraint("ck_planned_expense_states_shape",
            "(status = 'Confirmed' AND transaction_id IS NOT NULL) OR (status = 'Cancelled' AND transaction_id IS NULL)"));
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(s => s.UserId).HasColumnName("user_id");
        b.Property(s => s.PlannedExpenseId).HasColumnName("planned_expense_id");
        b.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.TransactionId).HasColumnName("transaction_id");
        b.Property(s => s.CreatedAtUtc).HasColumnName("created_at_utc");
        b.HasIndex(s => s.PlannedExpenseId).IsUnique().HasDatabaseName(ItemIndex);
        b.HasIndex(s => s.TransactionId).IsUnique();
        b.HasOne<PlannedExpense>().WithMany().HasForeignKey(s => new { s.PlannedExpenseId, s.UserId })
            .HasPrincipalKey(i => new { i.Id, i.UserId }).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Transaction>().WithMany().HasForeignKey(s => new { s.TransactionId, s.UserId })
            .HasPrincipalKey(t => new { t.Id, t.UserId }).OnDelete(DeleteBehavior.Cascade);
    }
}
