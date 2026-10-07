using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journal_entries", x => x.id);
                    table.CheckConstraint("ck_journal_entries_content", "length(btrim(content)) > 0 AND char_length(content) <= 20000");
                    table.CheckConstraint("ck_journal_entries_occurred_at", "occurred_at_utc >= TIMESTAMPTZ '0001-01-02 00:00:00+00' AND occurred_at_utc < TIMESTAMPTZ '9999-12-31 00:00:00+00'");
                    table.CheckConstraint("ck_journal_entries_title", "title IS NULL OR length(btrim(title)) > 0");
                    table.ForeignKey(
                        name: "FK_journal_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_user_timeline",
                table: "journal_entries",
                columns: new[] { "user_id", "occurred_at_utc", "created_at_utc", "id" },
                descending: new[] { false, true, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "journal_entries");
        }
    }
}
