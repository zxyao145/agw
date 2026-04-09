using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTableName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_agent_skill_relations",
                table: "agent_skill_relations");

            migrationBuilder.DropPrimaryKey(
                name: "pk_agent_mcp_tool_servers",
                table: "agent_mcp_tool_servers");

            migrationBuilder.RenameTable(
                name: "agent_skill_relations",
                newName: "agent_skill_relation");

            migrationBuilder.RenameTable(
                name: "agent_mcp_tool_servers",
                newName: "agent_mcp_server_relation");

            migrationBuilder.RenameIndex(
                name: "ix_agent_skill_relations_skill_id",
                table: "agent_skill_relation",
                newName: "ix_agent_skill_relation_skill_id");

            migrationBuilder.RenameIndex(
                name: "ix_agent_mcp_tool_servers_mcp_tool_server_id",
                table: "agent_mcp_server_relation",
                newName: "ix_agent_mcp_server_relation_mcp_tool_server_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agent_skill_relation",
                table: "agent_skill_relation",
                columns: new[] { "agent_id", "skill_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_agent_mcp_server_relation",
                table: "agent_mcp_server_relation",
                columns: new[] { "agent_id", "mcp_tool_server_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_agent_skill_relation",
                table: "agent_skill_relation");

            migrationBuilder.DropPrimaryKey(
                name: "pk_agent_mcp_server_relation",
                table: "agent_mcp_server_relation");

            migrationBuilder.RenameTable(
                name: "agent_skill_relation",
                newName: "agent_skill_relations");

            migrationBuilder.RenameTable(
                name: "agent_mcp_server_relation",
                newName: "agent_mcp_tool_servers");

            migrationBuilder.RenameIndex(
                name: "ix_agent_skill_relation_skill_id",
                table: "agent_skill_relations",
                newName: "ix_agent_skill_relations_skill_id");

            migrationBuilder.RenameIndex(
                name: "ix_agent_mcp_server_relation_mcp_tool_server_id",
                table: "agent_mcp_tool_servers",
                newName: "ix_agent_mcp_tool_servers_mcp_tool_server_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agent_skill_relations",
                table: "agent_skill_relations",
                columns: new[] { "agent_id", "skill_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_agent_mcp_tool_servers",
                table: "agent_mcp_tool_servers",
                columns: new[] { "agent_id", "mcp_tool_server_id" });
        }
    }
}
