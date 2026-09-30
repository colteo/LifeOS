using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    // Case-insensitive sibling-name uniqueness per user:
    //   (user_id, category_type, parent_category_id, lower(name)) NULLS NOT DISTINCT
    // EF Core cannot model an expression index, so this index is NOT part of the EF model: it is
    // created by raw SQL in the AddCategorySiblingUniqueness migration. EF migrations are the
    // canonical way to create the schema; EnsureCreated or a script generated from the model
    // would not contain it. CategoryRepository recognizes violations by this name.
    public const string SiblingNameIndexName = "ux_categories_user_sibling_name";

    // The parent reference; CategoryRepository recognizes its violations by name.
    public const string ParentForeignKeyName = "FK_categories_categories_parent_category_id_user_id";

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

        builder.Property(category => category.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // Target of the composite (…_id, user_id) foreign keys: references between categories and
        // from transactions can only point to a category of the same user.
        builder.HasAlternateKey(category => new { category.Id, category.UserId });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(category => category.UserId)
            .OnDelete(DeleteBehavior.Restrict);

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

        // (parent_category_id, user_id) → categories(id, user_id). A NULL parent skips the check.
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(category => new { category.ParentCategoryId, category.UserId })
            .HasPrincipalKey(parent => new { parent.Id, parent.UserId })
            .HasConstraintName(ParentForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
