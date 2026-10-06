using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_registrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    push_provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    push_token = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    inactive_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_registrations", x => x.id);
                    table.UniqueConstraint("ux_device_registrations_id_user", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_device_registrations_inactive_reason", "inactive_reason IS NULL OR inactive_reason IN ('PermissionDenied', 'SignedOut', 'TokenInvalid')");
                    table.CheckConstraint("ck_device_registrations_platform", "platform IN ('Android')");
                    table.CheckConstraint("ck_device_registrations_push_provider", "push_provider IN ('Fcm')");
                    table.CheckConstraint("ck_device_registrations_state", "(status = 'Active' AND push_token IS NOT NULL AND inactive_reason IS NULL) OR (status = 'Inactive' AND push_token IS NULL AND inactive_reason IS NOT NULL)");
                    table.CheckConstraint("ck_device_registrations_status", "status IN ('Active', 'Inactive')");
                    table.ForeignKey(
                        name: "FK_device_registrations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_registration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    notification_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.id);
                    table.CheckConstraint("ck_notification_deliveries_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_notification_deliveries_last_error_code", "last_error_code IS NULL OR last_error_code IN ('Transient', 'TokenInvalid', 'DeviceInactive', 'MaxAttempts', 'Expired', 'Rejected')");
                    table.CheckConstraint("ck_notification_deliveries_state", "(status = 'Pending' AND next_attempt_at_utc IS NOT NULL AND lease_expires_at_utc IS NULL AND sent_at_utc IS NULL) OR (status = 'Sending' AND next_attempt_at_utc IS NULL AND lease_expires_at_utc IS NOT NULL AND sent_at_utc IS NULL AND attempt_count >= 1) OR (status = 'Sent' AND next_attempt_at_utc IS NULL AND lease_expires_at_utc IS NULL AND sent_at_utc IS NOT NULL AND last_error_code IS NULL) OR (status = 'Failed' AND next_attempt_at_utc IS NULL AND lease_expires_at_utc IS NULL AND sent_at_utc IS NULL AND last_error_code IS NOT NULL)");
                    table.CheckConstraint("ck_notification_deliveries_status", "status IN ('Pending', 'Sending', 'Sent', 'Failed')");
                    table.CheckConstraint("ck_notification_deliveries_window", "expires_at_utc > created_at_utc");
                    table.ForeignKey(
                        name: "FK_notification_deliveries_device_registrations",
                        columns: x => new { x.device_registration_id, x.user_id },
                        principalTable: "device_registrations",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_source_execution",
                        column: x => x.source_execution_id,
                        principalTable: "automation_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_registrations_user_active",
                table: "device_registrations",
                column: "user_id",
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_device_registrations_installation_active",
                table: "device_registrations",
                column: "installation_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_device_registrations_installation_user",
                table: "device_registrations",
                columns: new[] { "installation_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_device_registrations_token",
                table: "device_registrations",
                columns: new[] { "push_provider", "push_token" },
                unique: true,
                filter: "push_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_device_registration_id_user_id",
                table: "notification_deliveries",
                columns: new[] { "device_registration_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_due",
                table: "notification_deliveries",
                column: "next_attempt_at_utc",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_source_execution_id",
                table: "notification_deliveries",
                column: "source_execution_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_stale",
                table: "notification_deliveries",
                column: "lease_expires_at_utc",
                filter: "status = 'Sending'");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_user_id",
                table: "notification_deliveries",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_notification_deliveries_device",
                table: "notification_deliveries",
                columns: new[] { "notification_key", "device_registration_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "device_registrations");
        }
    }
}
