using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Case-insensitive sibling-name uniqueness per user: a user cannot have two categories with the
    /// same name (ignoring case) under the same type and parent. Top-level categories (NULL parent)
    /// are siblings of each other, hence NULLS NOT DISTINCT (PostgreSQL 15+).
    ///
    /// Hand-written, PostgreSQL-specific SQL: EF Core cannot model an expression index such as
    /// lower(name), so this index is not part of the EF model or the model snapshot. EF migrations
    /// are the canonical way to create it; see CategoryConfiguration.SiblingNameIndexName.
    ///
    /// Fails if the table already contains such duplicates; resolve them before applying.
    /// </summary>
    public partial class AddCategorySiblingUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_categories_user_sibling_name
                    ON categories (user_id, category_type, parent_category_id, lower(name))
                    NULLS NOT DISTINCT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ux_categories_user_sibling_name;");
        }
    }
}
