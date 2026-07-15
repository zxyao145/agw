using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UseProjectContextsAndTaskRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM project_task_record;");

            migrationBuilder.DropTable(
                name: "project_task_session_binding");

            migrationBuilder.DropTable(
                name: "project_task");

            migrationBuilder.AddColumn<DateTime>(
                name: "finished_time",
                table: "project_task_record",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "job_id",
                table: "project_task_record",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_context_id",
                table: "project_task_record",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "project_task_record",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "task_error_message",
                table: "project_task_record",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_context",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: "Untitled"),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_context", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_session_binding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    external_agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    provider_session_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_session_binding", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_task_record_project_context_id",
                table: "project_task_record",
                column: "project_context_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_context_job_id",
                table: "project_context",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_context_project_id",
                table: "project_context",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_context_project_id_context_id",
                table: "project_context",
                columns: new[] { "project_id", "context_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_context_update_time",
                table: "project_context",
                column: "update_time");

            migrationBuilder.CreateIndex(
                name: "ix_task_session_binding_external_agent_name_provider_session_id",
                table: "task_session_binding",
                columns: new[] { "external_agent_name", "provider_session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_session_binding_task_id_agent_id_external_agent_name",
                table: "task_session_binding",
                columns: new[] { "task_id", "agent_id", "external_agent_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_context");

            migrationBuilder.DropTable(
                name: "task_session_binding");

            migrationBuilder.DropIndex(
                name: "ix_project_task_record_project_context_id",
                table: "project_task_record");

            migrationBuilder.DropColumn(
                name: "finished_time",
                table: "project_task_record");

            migrationBuilder.DropColumn(
                name: "job_id",
                table: "project_task_record");

            migrationBuilder.DropColumn(
                name: "project_context_id",
                table: "project_task_record");

            migrationBuilder.DropColumn(
                name: "status",
                table: "project_task_record");

            migrationBuilder.DropColumn(
                name: "task_error_message",
                table: "project_task_record");

            migrationBuilder.CreateTable(
                name: "project_task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    finished_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: "Untitled"),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_task", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_task_session_binding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    external_agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    provider_session_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_task_session_binding", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_task_context_id",
                table: "project_task",
                column: "context_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_task_project_id",
                table: "project_task",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_task_project_id_status_update_time",
                table: "project_task",
                columns: new[] { "project_id", "status", "update_time" });

            migrationBuilder.CreateIndex(
                name: "ix_project_task_session_binding_external_agent_name_provider_session_id",
                table: "project_task_session_binding",
                columns: new[] { "external_agent_name", "provider_session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_task_session_binding_task_id_agent_id_external_agent_name",
                table: "project_task_session_binding",
                columns: new[] { "task_id", "agent_id", "external_agent_name" },
                unique: true);
        }
    }
}
