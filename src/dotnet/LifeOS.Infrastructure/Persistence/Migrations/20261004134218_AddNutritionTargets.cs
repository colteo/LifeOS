using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nutrition_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    calories_kcal = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: true),
                    protein_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    carbs_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    fat_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_targets", x => x.id);
                    table.CheckConstraint("ck_nutrition_targets_effective_from", "effective_from BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
                    table.CheckConstraint("ck_nutrition_targets_source", "source IN ('Manual')");
                    table.CheckConstraint("ck_nutrition_targets_values", "(calories_kcal IS NULL OR (calories_kcal > 0 AND calories_kcal <= 10000)) AND (protein_grams IS NULL OR (protein_grams > 0 AND protein_grams <= 1000)) AND (carbs_grams IS NULL OR (carbs_grams > 0 AND carbs_grams <= 1000)) AND (fat_grams IS NULL OR (fat_grams > 0 AND fat_grams <= 1000))");
                    table.ForeignKey(
                        name: "FK_nutrition_targets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_nutrition_targets_user_effective_from",
                table: "nutrition_targets",
                columns: new[] { "user_id", "effective_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nutrition_targets");
        }
    }
}
