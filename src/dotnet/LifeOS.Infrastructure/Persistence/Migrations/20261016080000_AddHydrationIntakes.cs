using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHydrationIntakes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hydration_intakes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_ml = table.Column<int>(type: "integer", nullable: false),
                    diary_date = table.Column<DateOnly>(type: "date", nullable: false),
                    diary_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hydration_intakes", x => x.id);
                    table.CheckConstraint("ck_hydration_intakes_amount", "amount_ml BETWEEN 1 AND 4000");
                    table.CheckConstraint("ck_hydration_intakes_diary_date", "diary_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
                    table.CheckConstraint("ck_hydration_intakes_diary_time", "extract(second FROM diary_time) = 0");
                    table.CheckConstraint("ck_hydration_intakes_utc_offset", "(diary_date + diary_time) - (occurred_at_utc AT TIME ZONE 'UTC') BETWEEN interval '-840 minutes' AND interval '840 minutes'");
                    table.ForeignKey(
                        name: "FK_hydration_intakes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hydration_intakes_user_diary",
                table: "hydration_intakes",
                columns: new[] { "user_id", "diary_date", "diary_time", "id" },
                descending: new[] { false, false, true, true })
                .Annotation("Npgsql:IndexInclude", new[] { "amount_ml" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hydration_intakes");
        }
    }
}
