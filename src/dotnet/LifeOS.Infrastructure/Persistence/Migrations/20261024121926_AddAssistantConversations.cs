using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_sequence = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_conversations", x => x.id);
                    table.UniqueConstraint("ux_assistant_conversations_id_user", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_assistant_conversations_next_sequence", "next_sequence >= 1");
                    table.CheckConstraint("ck_assistant_conversations_updated_at", "updated_at_utc >= created_at_utc");
                    table.ForeignKey(
                        name: "FK_assistant_conversations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    client_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reply_to_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    turn_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    turn_attempts = table.Column<int>(type: "integer", nullable: true),
                    turn_lease_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_messages", x => x.id);
                    table.UniqueConstraint("ux_assistant_messages_conversation_id", x => new { x.conversation_id, x.id });
                    table.CheckConstraint("ck_assistant_messages_content", "length(btrim(content)) > 0 AND char_length(content) <= CASE WHEN role = 'User' THEN 1000 ELSE 4000 END");
                    table.CheckConstraint("ck_assistant_messages_failure_code", "failure_code IS NULL OR failure_code ~ '^[a-z0-9_]{1,64}$'");
                    table.CheckConstraint("ck_assistant_messages_role", "role IN ('User', 'Assistant')");
                    table.CheckConstraint("ck_assistant_messages_sequence", "sequence >= 1");
                    table.CheckConstraint("ck_assistant_messages_shape", "(role = 'User' AND client_message_id IS NOT NULL AND reply_to_message_id IS NULL\n    AND turn_status IS NOT NULL AND turn_attempts >= 1)\nOR (role = 'Assistant' AND client_message_id IS NULL AND reply_to_message_id IS NOT NULL\n    AND turn_status IS NULL AND turn_attempts IS NULL AND turn_lease_until_utc IS NULL AND failure_code IS NULL)");
                    table.CheckConstraint("ck_assistant_messages_turn", "turn_status IS NULL OR ((turn_status = 'Processing') = (turn_lease_until_utc IS NOT NULL) AND (turn_status = 'Failed') = (failure_code IS NOT NULL))");
                    table.CheckConstraint("ck_assistant_messages_turn_status", "turn_status IS NULL OR turn_status IN ('Processing', 'Completed', 'Failed')");
                    table.ForeignKey(
                        name: "FK_assistant_messages_conversations",
                        columns: x => new { x.conversation_id, x.user_id },
                        principalTable: "assistant_conversations",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assistant_messages_reply_to",
                        columns: x => new { x.conversation_id, x.reply_to_message_id },
                        principalTable: "assistant_messages",
                        principalColumns: new[] { "conversation_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_conversations_user_updated",
                table: "assistant_conversations",
                columns: new[] { "user_id", "updated_at_utc", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_assistant_conversations_user_client",
                table: "assistant_conversations",
                columns: new[] { "user_id", "client_conversation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_messages_conversation_id_user_id",
                table: "assistant_messages",
                columns: new[] { "conversation_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_assistant_messages_conversation_client",
                table: "assistant_messages",
                columns: new[] { "conversation_id", "client_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_assistant_messages_conversation_processing",
                table: "assistant_messages",
                column: "conversation_id",
                unique: true,
                filter: "turn_status = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "ux_assistant_messages_conversation_reply",
                table: "assistant_messages",
                columns: new[] { "conversation_id", "reply_to_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_assistant_messages_conversation_sequence",
                table: "assistant_messages",
                columns: new[] { "conversation_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_messages");

            migrationBuilder.DropTable(
                name: "assistant_conversations");
        }
    }
}
