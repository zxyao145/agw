using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
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
                name: "ix_event_subscription_create_by_managed_by_extension_id_manage",
                table: "event_subscription"
            );

            migrationBuilder.DropColumn(name: "job_id", table: "project_conversation_chat_history");

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
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "native"
            );

            migrationBuilder.AddColumn<string>(
                name: "managed_by",
                table: "event_subscription",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "user"
            );

            migrationBuilder.Sql(
                "UPDATE event_subscription SET managed_by = 'ext:' || managed_by_extension_id::text "
                    + "WHERE managed_by_extension_id IS NOT NULL;"
            );

            migrationBuilder.DropColumn(name: "managed_by_extension_id", table: "event_subscription");

            migrationBuilder.AddColumn<string>(
                name: "managed_by",
                table: "event_source",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "user"
            );

            migrationBuilder.AddColumn<string>(
                name: "managed_key",
                table: "event_source",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "turn_id",
                table: "event_delivery_attempt",
                type: "uuid",
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

            migrationBuilder.DropColumn(name: "kind", table: "project_conversation");

            migrationBuilder.AddColumn<Guid>(
                name: "managed_by_extension_id",
                table: "event_subscription",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.Sql(
                "UPDATE event_subscription SET managed_by_extension_id = substring(managed_by from 5)::uuid "
                    + "WHERE managed_by LIKE 'ext:%';"
            );

            migrationBuilder.DropColumn(name: "managed_by", table: "event_subscription");

            migrationBuilder.DropColumn(name: "managed_by", table: "event_source");

            migrationBuilder.DropColumn(name: "managed_key", table: "event_source");

            migrationBuilder.DropColumn(name: "turn_id", table: "event_delivery_attempt");

            migrationBuilder.RenameColumn(name: "relate_id", table: "project_conversation", newName: "job_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_relate_id",
                table: "project_conversation",
                newName: "ix_project_conversation_job_id"
            );

            migrationBuilder.AddColumn<Guid>(
                name: "job_id",
                table: "project_conversation_chat_history",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    active_attempt_started_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    active_turn_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_type = table.Column<int>(type: "integer", nullable: true),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    max_retry_count = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    next_run_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    row_version = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    trigger_type = table.Column<int>(type: "integer", nullable: false),
                    trigger_value = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(
                        type: "character varying(2000)",
                        maxLength: 2000,
                        nullable: true
                    ),
                    start_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_log", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_subscription_create_by_managed_by_extension_id_manage",
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
