using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameTable2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agentflow_edges_agentflows_agentflow_id",
                table: "agentflow_edges");

            migrationBuilder.DropForeignKey(
                name: "fk_agentflow_nodes_agentflows_agentflow_id",
                table: "agentflow_nodes");

            migrationBuilder.AddForeignKey(
                name: "fk_agentflow_edges_agent_flow_agentflow_id",
                table: "agentflow_edges",
                column: "agentflow_id",
                principalTable: "agentflow",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_agentflow_nodes_agent_flow_agentflow_id",
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
                name: "fk_agentflow_edges_agent_flow_agentflow_id",
                table: "agentflow_edges");

            migrationBuilder.DropForeignKey(
                name: "fk_agentflow_nodes_agent_flow_agentflow_id",
                table: "agentflow_nodes");

            migrationBuilder.AddForeignKey(
                name: "fk_agentflow_edges_agentflows_agentflow_id",
                table: "agentflow_edges",
                column: "agentflow_id",
                principalTable: "Agentflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_agentflow_nodes_agentflows_agentflow_id",
                table: "agentflow_nodes",
                column: "agentflow_id",
                principalTable: "Agentflows",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
