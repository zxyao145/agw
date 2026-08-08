using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildingBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "building_blocks",
                table: "project",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "building_blocks",
                table: "agent",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_file_memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_file_memory", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_session_state",
                columns: table => new
                {
                    project_context_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_node_id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    serialized_session = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_session_state", x => new { x.project_context_id, x.agent_id, x.agentflow_node_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_file_memory_project_id_agent_id_path",
                table: "agent_file_memory",
                columns: new[] { "project_id", "agent_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_file_memory_updated_at",
                table: "agent_file_memory",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_state_agent_id",
                table: "agent_session_state",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_state_updated_at",
                table: "agent_session_state",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_file_memory");

            migrationBuilder.DropTable(
                name: "agent_session_state");

            migrationBuilder.DropColumn(
                name: "building_blocks",
                table: "project");

            migrationBuilder.DropColumn(
                name: "building_blocks",
                table: "agent");
        }
    }
}
