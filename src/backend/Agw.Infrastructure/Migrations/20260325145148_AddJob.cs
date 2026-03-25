using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_type = table.Column<int>(type: "INTEGER", nullable: true),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    prompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    trigger_type = table.Column<int>(type: "INTEGER", nullable: false),
                    trigger_value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    time_zone_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    next_run_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    retry_count = table.Column<int>(type: "INTEGER", nullable: false),
                    max_retry_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    row_version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "o_auth_authorization_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    access_token = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    refresh_token = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    token_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    scope = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_o_auth_authorization_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_execution_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    start_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    end_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    success = table.Column<bool>(type: "INTEGER", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_execution_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_next_run_time",
                table: "jobs",
                columns: new[] { "is_enabled", "status", "next_run_time" });

            migrationBuilder.CreateIndex(
                name: "ix_task_project",
                table: "jobs",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_o_auth_authorization_tokens_expires_at_utc",
                table: "o_auth_authorization_tokens",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_o_auth_authorization_tokens_provider",
                table: "o_auth_authorization_tokens",
                column: "provider");

            migrationBuilder.CreateIndex(
                name: "ix_o_auth_authorization_tokens_provider_subject",
                table: "o_auth_authorization_tokens",
                columns: new[] { "provider", "subject" });

            migrationBuilder.CreateIndex(
                name: "ix_task_execution_logs_task_id_start_time",
                table: "task_execution_logs",
                columns: new[] { "task_id", "start_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "o_auth_authorization_tokens");

            migrationBuilder.DropTable(
                name: "task_execution_logs");
        }
    }
}
