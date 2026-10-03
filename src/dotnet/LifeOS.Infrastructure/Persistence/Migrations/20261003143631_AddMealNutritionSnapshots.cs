using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMealNutritionSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meal_nutrition_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    meal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    calories_kcal = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: false),
                    protein_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    carbs_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    fat_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meal_nutrition_snapshots", x => x.id);
                    table.CheckConstraint("ck_meal_nutrition_snapshots_source", "source IN ('AiConfirmed', 'AiRequested', 'AiAutoClosed', 'UserAdjusted')");
                    table.CheckConstraint("ck_meal_nutrition_snapshots_values", "calories_kcal BETWEEN 0 AND 10000 AND protein_grams BETWEEN 0 AND 1000 AND carbs_grams BETWEEN 0 AND 1000 AND fat_grams BETWEEN 0 AND 1000");
                    table.ForeignKey(
                        name: "FK_meal_nutrition_snapshots_meal_entries_meal_entry_id",
                        column: x => x.meal_entry_id,
                        principalTable: "meal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_meal_nutrition_snapshots_meal_entry",
                table: "meal_nutrition_snapshots",
                column: "meal_entry_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meal_nutrition_snapshots");
        }
    }
}
