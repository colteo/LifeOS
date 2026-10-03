using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionMealEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meal_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    meal_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    diary_date = table.Column<DateOnly>(type: "date", nullable: false),
                    diary_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meal_entries", x => x.id);
                    table.CheckConstraint("ck_meal_entries_description", "length(btrim(description)) > 0 AND char_length(description) <= 2000");
                    table.CheckConstraint("ck_meal_entries_diary_date", "diary_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
                    table.CheckConstraint("ck_meal_entries_diary_time", "extract(second FROM diary_time) = 0");
                    table.CheckConstraint("ck_meal_entries_meal_type", "meal_type IS NULL OR meal_type IN ('Breakfast', 'Lunch', 'Dinner', 'Snack', 'Other')");
                    table.CheckConstraint("ck_meal_entries_utc_offset", "(diary_date + diary_time) - (occurred_at_utc AT TIME ZONE 'UTC') BETWEEN interval '-840 minutes' AND interval '840 minutes'");
                    table.ForeignKey(
                        name: "FK_meal_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_meal_entries_user_diary",
                table: "meal_entries",
                columns: new[] { "user_id", "diary_date", "diary_time", "id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meal_entries");
        }
    }
}
