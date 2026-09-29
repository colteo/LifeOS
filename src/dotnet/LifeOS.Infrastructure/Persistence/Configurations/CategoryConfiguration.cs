using LifeOS.Domain.Finance.Categories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories", table =>
            table.HasCheckConstraint(
                "ck_categories_parent_not_self",
                "parent_category_id IS NULL OR parent_category_id <> id"));

        builder.HasKey(category => category.Id);

        builder.Property(category => category.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(category => category.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(category => category.CategoryType)
            .HasColumnName("category_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(category => category.ParentCategoryId)
            .HasColumnName("parent_category_id");

        builder.Property(category => category.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(category => category.ParentCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
