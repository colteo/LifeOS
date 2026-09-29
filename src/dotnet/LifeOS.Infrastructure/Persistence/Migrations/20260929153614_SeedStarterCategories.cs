using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Inserts the starter categories once, as ordinary rows of the categories table.
    /// The seed is deliberately not modelled with HasData: after this migration the rows
    /// are normal user data and later model changes never rewrite or delete them.
    /// Ids and the creation timestamp are fixed so every environment gets the same rows.
    /// </summary>
    public partial class SeedStarterCategories : Migration
    {
        private static readonly DateTimeOffset SeededAtUtc = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);

        private static readonly (Guid Id, string Name, string CategoryType)[] StarterCategories =
        [
            (new Guid("01a0ea76-0c00-7001-8c1f-e05eed000001"), "Casa", "Expense"),
            (new Guid("01a0ea76-0c00-7002-8c1f-e05eed000002"), "Auto", "Expense"),
            (new Guid("01a0ea76-0c00-7003-8c1f-e05eed000003"), "Alimentari", "Expense"),
            (new Guid("01a0ea76-0c00-7004-8c1f-e05eed000004"), "Mangiare fuori", "Expense"),
            (new Guid("01a0ea76-0c00-7005-8c1f-e05eed000005"), "Salute", "Expense"),
            (new Guid("01a0ea76-0c00-7006-8c1f-e05eed000006"), "Shopping", "Expense"),
            (new Guid("01a0ea76-0c00-7007-8c1f-e05eed000007"), "Viaggi", "Expense"),
            (new Guid("01a0ea76-0c00-7008-8c1f-e05eed000008"), "Regali", "Expense"),
            (new Guid("01a0ea76-0c00-7009-8c1f-e05eed000009"), "Telefonia", "Expense"),
            (new Guid("01a0ea76-0c00-700a-8c1f-e05eed00000a"), "Altro", "Expense"),
            (new Guid("01a0ea76-0c00-700b-8c1f-e05eed00000b"), "Stipendio", "Income"),
            (new Guid("01a0ea76-0c00-700c-8c1f-e05eed00000c"), "Bonus", "Income"),
            (new Guid("01a0ea76-0c00-700d-8c1f-e05eed00000d"), "Rimborso", "Income"),
            (new Guid("01a0ea76-0c00-700e-8c1f-e05eed00000e"), "Regalo", "Income"),
            (new Guid("01a0ea76-0c00-700f-8c1f-e05eed00000f"), "Altro", "Income"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (id, name, categoryType) in StarterCategories)
            {
                migrationBuilder.InsertData(
                    table: "categories",
                    columns: ["id", "name", "category_type", "parent_category_id", "created_at_utc"],
                    values: [id, name, categoryType, null, SeededAtUtc]);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fails (FK RESTRICT) if user-created subcategories still reference a starter category.
            foreach (var (id, _, _) in StarterCategories)
            {
                migrationBuilder.DeleteData(
                    table: "categories",
                    keyColumn: "id",
                    keyValue: id);
            }
        }
    }
}
