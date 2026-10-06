using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "weekly_review_settings",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_review_settings", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_weekly_review_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "weekly_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    week_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    generated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    data_version = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_reviews", x => x.id);
                    table.CheckConstraint("ck_weekly_reviews_data_version", "data_version >= 1");
                    table.CheckConstraint("ck_weekly_reviews_snapshot", "jsonb_typeof(snapshot) = 'object'");
                    table.CheckConstraint("ck_weekly_reviews_week", "week_end_date = week_start_date + 6 AND EXTRACT(ISODOW FROM week_end_date) = 7");
                    table.ForeignKey(
                        name: "FK_weekly_reviews_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_weekly_reviews_user_week_end",
                table: "weekly_reviews",
                columns: new[] { "user_id", "week_end_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "weekly_review_settings");

            migrationBuilder.DropTable(
                name: "weekly_reviews");
        }
    }
}
