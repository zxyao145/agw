using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAppRelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_app_relation",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    app_instance_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_app_relation", x => new { x.agent_id, x.app_instance_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_app_relation_app_instance_id",
                table: "agent_app_relation",
                column: "app_instance_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_app_relation");
        }
    }
}
