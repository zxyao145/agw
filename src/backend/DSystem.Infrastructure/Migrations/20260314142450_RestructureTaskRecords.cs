using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestructureTaskRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_session_records");

            migrationBuilder.DropIndex(
                name: "ix_project_tasks_project_id_session_id",
                table: "project_tasks");

            migrationBuilder.DropIndex(
                name: "ix_project_tasks_project_id1",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "agent_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "agent_type",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "agentflow_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "input",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "project_id1",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "started_time",
                table: "project_tasks");

            migrationBuilder.RenameColumn(
                name: "session_id",
                table: "project_tasks",
                newName: "context_id");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "project_tasks",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "Untitled",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 200,
                oldDefaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "system_prompt",
                table: "project_tasks",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_project_tasks_context_id",
                table: "project_tasks",
                column: "context_id");

            migrationBuilder.CreateTable(
                name: "task_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    agent_type = table.Column<int>(type: "INTEGER", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    input = table.Column<string>(type: "jsonb", nullable: false),
                    messages = table.Column<string>(type: "jsonb", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_tasks_context_id",
                table: "project_tasks",
                column: "context_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_tasks_project_id",
                table: "project_tasks",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_records_context_id_create_time",
                table: "task_records",
                columns: new[] { "context_id", "create_time" });

            migrationBuilder.CreateIndex(
                name: "ix_task_records_context_id_session_id_create_time",
                table: "task_records",
                columns: new[] { "context_id", "session_id", "create_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_records");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_project_tasks_context_id",
                table: "project_tasks");

            migrationBuilder.DropIndex(
                name: "ix_project_tasks_context_id",
                table: "project_tasks");

            migrationBuilder.DropIndex(
                name: "ix_project_tasks_project_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "system_prompt",
                table: "project_tasks");

            migrationBuilder.RenameColumn(
                name: "context_id",
                table: "project_tasks",
                newName: "session_id");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "project_tasks",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 200,
                oldDefaultValue: "Untitled");

            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                table: "project_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "agent_type",
                table: "project_tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "agentflow_id",
                table: "project_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "input",
                table: "project_tasks",
                type: "TEXT",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "project_id1",
                table: "project_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "started_time",
                table: "project_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_session_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    contents = table.Column<string>(type: "jsonb", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    message_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    project_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    role = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_session_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_tasks_project_id_session_id",
                table: "project_tasks",
                columns: new[] { "project_id", "session_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_tasks_project_id1",
                table: "project_tasks",
                column: "project_id1");

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_records_project_id_session_id_message_id",
                table: "agent_session_records",
                columns: new[] { "project_id", "session_id", "message_id" },
                unique: true);
        }
    }
}
