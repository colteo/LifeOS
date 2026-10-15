using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHydrationTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hydration_settings",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quick_add_1_ml = table.Column<int>(type: "integer", nullable: false),
                    quick_add_2_ml = table.Column<int>(type: "integer", nullable: false),
                    quick_add_3_ml = table.Column<int>(type: "integer", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hydration_settings", x => x.user_id);
                    table.CheckConstraint("ck_hydration_settings_quick_add", "quick_add_1_ml BETWEEN 1 AND 4000 AND quick_add_2_ml BETWEEN 1 AND 4000 AND quick_add_3_ml BETWEEN 1 AND 4000");
                    table.ForeignKey(
                        name: "FK_hydration_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hydration_target_versions",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    target_ml = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hydration_target_versions", x => new { x.user_id, x.effective_from });
                    table.CheckConstraint("ck_hydration_target_versions_effective_from", "effective_from BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
                    table.CheckConstraint("ck_hydration_target_versions_target", "target_ml IS NULL OR target_ml BETWEEN 250 AND 10000");
                    table.ForeignKey(
                        name: "FK_hydration_target_versions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hydration_settings");

            migrationBuilder.DropTable(
                name: "hydration_target_versions");
        }
    }
}
