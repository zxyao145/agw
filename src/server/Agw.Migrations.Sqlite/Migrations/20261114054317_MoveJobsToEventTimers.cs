using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MoveJobsToEventTimers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "job_log");

            migrationBuilder.DropTable(name: "job");

            migrationBuilder.DropIndex(
                name: "ix_event_subscription_create_by_managed_by_extension_id_managed_key",
                table: "event_subscription"
            );

            // 使用 SQLite 原生 DROP COLUMN，避免重建表时引入外键。
            // SQLite's native DROP COLUMN avoids a table rebuild that would introduce a foreign key.
            migrationBuilder.Sql("ALTER TABLE \"project_conversation_chat_history\" DROP COLUMN \"job_id\";");

            migrationBuilder.RenameColumn(name: "job_id", table: "project_conversation", newName: "relate_id");

            migrationBuilder.Sql("UPDATE project_conversation SET relate_id = NULL WHERE relate_id IS NOT NULL;");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_job_id",
                table: "project_conversation",
                newName: "ix_project_conversation_relate_id"
            );

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "project_conversation",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "native"
            );

            migrationBuilder.AddColumn<string>(
                name: "managed_by",
                table: "event_subscription",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "user"
            );

            // EF 在 SQLite 中把 Guid 保存为大写文本，managed_by 使用小写的 D 格式。
            // EF stores Guid values in SQLite as uppercase text; managed_by uses the lowercase D format.
            migrationBuilder.Sql(
                "UPDATE event_subscription SET managed_by = 'ext:' || lower(managed_by_extension_id) "
                    + "WHERE managed_by_extension_id IS NOT NULL;"
            );

            migrationBuilder.Sql("ALTER TABLE \"event_subscription\" DROP COLUMN \"managed_by_extension_id\";");

            migrationBuilder.AddColumn<string>(
                name: "managed_by",
                table: "event_source",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "user"
            );

            migrationBuilder.AddColumn<string>(
                name: "managed_key",
                table: "event_source",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "turn_id",
                table: "event_delivery_attempt",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_subscription_create_by_managed_by_managed_key",
                table: "event_subscription",
                columns: new[] { "create_by", "managed_by", "managed_key" },
                unique: true,
                filter: "managed_key IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_source_create_by_managed_by_managed_key",
                table: "event_source",
                columns: new[] { "create_by", "managed_by", "managed_key" },
                unique: true,
                filter: "managed_key IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_delivery_attempt_delivery_id_attempt",
                table: "event_delivery_attempt",
                columns: new[] { "delivery_id", "attempt" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_delivery_subscription_id_id",
                table: "event_delivery",
                columns: new[] { "subscription_id", "id" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_event_subscription_create_by_managed_by_managed_key",
                table: "event_subscription"
            );

            migrationBuilder.DropIndex(name: "ix_event_source_create_by_managed_by_managed_key", table: "event_source");

            migrationBuilder.DropIndex(
                name: "ix_event_delivery_attempt_delivery_id_attempt",
                table: "event_delivery_attempt"
            );

            migrationBuilder.DropIndex(name: "ix_event_delivery_subscription_id_id", table: "event_delivery");

            migrationBuilder.Sql("ALTER TABLE \"project_conversation\" DROP COLUMN \"kind\";");

            migrationBuilder.AddColumn<Guid>(
                name: "managed_by_extension_id",
                table: "event_subscription",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.Sql(
                "UPDATE event_subscription SET managed_by_extension_id = upper(substr(managed_by, 5)) "
                    + "WHERE managed_by LIKE 'ext:%';"
            );

            migrationBuilder.Sql("ALTER TABLE \"event_subscription\" DROP COLUMN \"managed_by\";");

            migrationBuilder.Sql("ALTER TABLE \"event_source\" DROP COLUMN \"managed_by\";");

            migrationBuilder.Sql("ALTER TABLE \"event_source\" DROP COLUMN \"managed_key\";");

            migrationBuilder.Sql("ALTER TABLE \"event_delivery_attempt\" DROP COLUMN \"turn_id\";");

            migrationBuilder.RenameColumn(name: "relate_id", table: "project_conversation", newName: "job_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_relate_id",
                table: "project_conversation",
                newName: "ix_project_conversation_job_id"
            );

            migrationBuilder.AddColumn<Guid>(
                name: "job_id",
                table: "project_conversation_chat_history",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    active_attempt_started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    active_turn_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    agent_type = table.Column<int>(type: "INTEGER", nullable: true),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    max_retry_count = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    next_run_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    prompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    retry_count = table.Column<int>(type: "INTEGER", nullable: false),
                    row_version = table.Column<byte[]>(type: "BLOB", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    trigger_type = table.Column<int>(type: "INTEGER", nullable: false),
                    trigger_value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job", x => x.id);
                    table.CheckConstraint(
                        "ck_job_active_attempt",
                        "(status = 2 AND active_turn_id IS NOT NULL AND active_attempt_started_at IS NOT NULL) OR (status <> 2 AND active_turn_id IS NULL AND active_attempt_started_at IS NULL)"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "job_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    end_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    start_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    success = table.Column<bool>(type: "INTEGER", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_log", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_subscription_create_by_managed_by_extension_id_managed_key",
                table: "event_subscription",
                columns: new[] { "create_by", "managed_by_extension_id", "managed_key" },
                unique: true,
                filter: "managed_key IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_job_active_turn_id",
                table: "job",
                column: "active_turn_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_task_next_run_time",
                table: "job",
                columns: new[] { "is_enabled", "status", "next_run_time" }
            );

            migrationBuilder.CreateIndex(name: "ix_task_project", table: "job", column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_log_job_id_start_time",
                table: "job_log",
                columns: new[] { "job_id", "start_time" }
            );
        }
    }
}
