using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameTable3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agentflow_edges_agent_flow_agentflow_id",
                table: "agentflow_edges");

            migrationBuilder.DropForeignKey(
                name: "fk_agentflow_nodes_agent_flow_agentflow_id",
                table: "agentflow_nodes");

            migrationBuilder.DropPrimaryKey(
                name: "pk_agentflows",
                table: "Agentflows");

            migrationBuilder.RenameTable(
                name: "Agentflows",
                newName: "agentflow");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agentflow",
                table: "agentflow",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_agentflow_edges_agentflow_agentflow_id",
                table: "agentflow_edges",
                column: "agentflow_id",
                principalTable: "agentflow",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_agentflow_nodes_agentflow_agentflow_id",
                table: "agentflow_nodes",
                column: "agentflow_id",
                principalTable: "agentflow",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agentflow_edges_agentflow_agentflow_id",
                table: "agentflow_edges");

            migrationBuilder.DropForeignKey(
                name: "fk_agentflow_nodes_agentflow_agentflow_id",
                table: "agentflow_nodes");

            migrationBuilder.DropPrimaryKey(
                name: "pk_agentflow",
                table: "agentflow");

            migrationBuilder.RenameTable(
                name: "agentflow",
                newName: "Agentflows");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agentflows",
                table: "Agentflows",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_agentflow_edges_agent_flow_agentflow_id",
                table: "agentflow_edges",
                column: "agentflow_id",
                principalTable: "Agentflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_agentflow_nodes_agent_flow_agentflow_id",
                table: "agentflow_nodes",
                column: "agentflow_id",
                principalTable: "Agentflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
