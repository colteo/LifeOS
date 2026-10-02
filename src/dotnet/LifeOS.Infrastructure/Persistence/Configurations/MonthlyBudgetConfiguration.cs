using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class MonthlyBudgetConfiguration : IEntityTypeConfiguration<MonthlyBudget>
{
    public void Configure(EntityTypeBuilder<MonthlyBudget> builder)
    {
        builder.ToTable("monthly_budgets", table =>
        {
            table.HasCheckConstraint("ck_monthly_budgets_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_monthly_budgets_year_month", "year BETWEEN 1 AND 9998 AND month BETWEEN 1 AND 12");
            table.HasCheckConstraint("ck_monthly_budgets_currency", "currency ~ '^[A-Z]{3}$'");
        });
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(b => b.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(b => b.Year).HasColumnName("year").IsRequired();
        builder.Property(b => b.Month).HasColumnName("month").IsRequired();
        builder.Property(b => b.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        builder.Property(b => b.Amount).HasColumnName("amount").HasPrecision(19, 4).IsRequired();
        builder.HasIndex(b => new { b.UserId, b.Year, b.Month, b.Currency })
            .IsUnique().HasDatabaseName("ux_monthly_budgets_user_month_currency");
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
