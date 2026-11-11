using Agw.Extensions.Sdk.Database;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Extensions.Feishu.Migrations.Sqlite
{
    /// <inheritdoc />
    [ExtensionMigrationProvider(ExtensionDatabaseProvider.Sqlite)]
    public partial class FeishuSqliteInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48f112___inbox",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    processed_at = table.Column<long>(type: "INTEGER", nullable: false),
                    result_json = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48f112___inbox", x => x.delivery_id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48f112___outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    result_json = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<int>(type: "INTEGER", nullable: true),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48f112___outbox", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48f112_app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    open_domain = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    webhook_url = table.Column<string>(type: "text", nullable: true),
                    webhook_secret = table.Column<string>(type: "text", nullable: true),
                    app_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    app_secret = table.Column<string>(type: "text", nullable: true),
                    encrypt_key = table.Column<string>(type: "text", nullable: true),
                    verification_token = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48f112_app", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48f112_bind_request",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    code_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    open_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    app_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48f112_bind_request", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48f112_callback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    open_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    notification_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    message_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    outbox_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    option = table.Column<int>(type: "INTEGER", nullable: true),
                    text = table.Column<string>(type: "text", nullable: true),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    app_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48f112_callback", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48f112_identity_binding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    open_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    app_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48f112_identity_binding", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48f112_message",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    dispatch_key = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    notification_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    message_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    card_version = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    app_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48f112_message", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48f112_target",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    receive_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    app_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48f112_target", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_uq_outbox_key",
                table: "zext_019a4c175f007a808b3d2b732f48f112___outbox",
                column: "idempotency_key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_ix_bind_app_exp",
                table: "zext_019a4c175f007a808b3d2b732f48f112_bind_request",
                columns: new[] { "app_id", "expires_at" }
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_uq_bind_request_code_hash",
                table: "zext_019a4c175f007a808b3d2b732f48f112_bind_request",
                column: "code_hash",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_uq_callback_event_id",
                table: "zext_019a4c175f007a808b3d2b732f48f112_callback",
                column: "event_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_uq_identity_app_open",
                table: "zext_019a4c175f007a808b3d2b732f48f112_identity_binding",
                columns: new[] { "app_id", "open_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_uq_message_app_message",
                table: "zext_019a4c175f007a808b3d2b732f48f112_message",
                columns: new[] { "app_id", "message_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_uq_message_dispatch_key",
                table: "zext_019a4c175f007a808b3d2b732f48f112_message",
                column: "dispatch_key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_ix_target_app",
                table: "zext_019a4c175f007a808b3d2b732f48f112_target",
                column: "app_id"
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48f112_uq_target_kind_receive",
                table: "zext_019a4c175f007a808b3d2b732f48f112_target",
                columns: new[] { "app_id", "kind", "receive_id" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48f112___inbox");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48f112___outbox");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48f112_app");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48f112_bind_request");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48f112_callback");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48f112_identity_binding");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48f112_message");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48f112_target");
        }
    }
}
