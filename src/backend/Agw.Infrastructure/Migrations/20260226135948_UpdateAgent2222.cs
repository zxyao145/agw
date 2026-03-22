using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAgent2222 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agent_session_records_project_id_session_id",
                table: "agent_session_records");

            migrationBuilder.DropColumn(
                name: "messages",
                table: "agent_session_records");

            migrationBuilder.DropColumn(
                name: "title",
                table: "agent_session_records");

            migrationBuilder.AddColumn<Guid>(
                name: "project_id1",
                table: "project_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "session_id",
                table: "project_tasks",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "project_tasks",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "author",
                table: "agent_session_records",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contents",
                table: "agent_session_records",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "error",
                table: "agent_session_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "message_id",
                table: "agent_session_records",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "metadata",
                table: "agent_session_records",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "agent_session_records",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "agent_mcp_tool_servers",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    mcp_tool_server_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_mcp_tool_servers", x => new { x.agent_id, x.mcp_tool_server_id });
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

            migrationBuilder.CreateIndex(
                name: "ix_agent_mcp_tool_servers_mcp_tool_server_id",
                table: "agent_mcp_tool_servers",
                column: "mcp_tool_server_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_mcp_tool_servers");

            migrationBuilder.DropIndex(
                name: "ix_project_tasks_project_id_session_id",
                table: "project_tasks");

            migrationBuilder.DropIndex(
                name: "ix_project_tasks_project_id1",
                table: "project_tasks");

            migrationBuilder.DropIndex(
                name: "ix_agent_session_records_project_id_session_id_message_id",
                table: "agent_session_records");

            migrationBuilder.DropColumn(
                name: "project_id1",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "title",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "author",
                table: "agent_session_records");

            migrationBuilder.DropColumn(
                name: "contents",
                table: "agent_session_records");

            migrationBuilder.DropColumn(
                name: "error",
                table: "agent_session_records");

            migrationBuilder.DropColumn(
                name: "message_id",
                table: "agent_session_records");

            migrationBuilder.DropColumn(
                name: "metadata",
                table: "agent_session_records");

            migrationBuilder.DropColumn(
                name: "role",
                table: "agent_session_records");

            migrationBuilder.AddColumn<string>(
                name: "messages",
                table: "agent_session_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "agent_session_records",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_records_project_id_session_id",
                table: "agent_session_records",
                columns: new[] { "project_id", "session_id" },
                unique: true);
        }
    }
}
