using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    draft_version = table.Column<int>(type: "integer", nullable: false),
                    payload_version = table.Column<int>(type: "integer", nullable: false),
                    draft = table.Column<string>(type: "jsonb", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    executed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_operations", x => x.id);
                    table.CheckConstraint("ck_assistant_operations_draft", "draft_version >= 1 AND payload_version >= 1 AND jsonb_typeof(draft) = 'object'");
                    table.CheckConstraint("ck_assistant_operations_kind", "kind IN ('finance.transaction.create')");
                    table.CheckConstraint("ck_assistant_operations_result", "(status = 'Executed') = (result_transaction_id IS NOT NULL) AND (status = 'Executed') = (executed_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_assistant_operations_status", "status IN ('Collecting', 'AwaitingConfirmation', 'Executed', 'Cancelled', 'Expired')");
                    table.CheckConstraint("ck_assistant_operations_times", "updated_at_utc >= created_at_utc AND expires_at_utc > created_at_utc");
                    table.ForeignKey(
                        name: "FK_assistant_operations_conversations",
                        columns: x => new { x.conversation_id, x.user_id },
                        principalTable: "assistant_conversations",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_operations_conversation",
                table: "assistant_operations",
                columns: new[] { "conversation_id", "user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_assistant_operations_conversation_open",
                table: "assistant_operations",
                column: "conversation_id",
                unique: true,
                filter: "status IN ('Collecting', 'AwaitingConfirmation')");

            migrationBuilder.CreateIndex(
                name: "ux_assistant_operations_result_transaction",
                table: "assistant_operations",
                column: "result_transaction_id",
                unique: true,
                filter: "result_transaction_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_operations");
        }
    }
}
