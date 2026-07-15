using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "environment_variables",
                table: "project",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "tools",
                table: "project",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_app_relation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    app_instance_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_app_relation", x => new { x.project_id, x.app_instance_id });
                });

            migrationBuilder.CreateTable(
                name: "project_mcp_server_relation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    mcp_tool_server_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_mcp_server_relation", x => new { x.project_id, x.mcp_tool_server_id });
                });

            migrationBuilder.CreateTable(
                name: "project_skill_relation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    skill_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_skill_relation", x => new { x.project_id, x.skill_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_app_relation_app_instance_id",
                table: "project_app_relation",
                column: "app_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_mcp_server_relation_mcp_tool_server_id",
                table: "project_mcp_server_relation",
                column: "mcp_tool_server_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_skill_relation_skill_id",
                table: "project_skill_relation",
                column: "skill_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_app_relation");

            migrationBuilder.DropTable(
                name: "project_mcp_server_relation");

            migrationBuilder.DropTable(
                name: "project_skill_relation");

            migrationBuilder.DropColumn(
                name: "environment_variables",
                table: "project");

            migrationBuilder.DropColumn(
                name: "tools",
                table: "project");
        }
    }
}
