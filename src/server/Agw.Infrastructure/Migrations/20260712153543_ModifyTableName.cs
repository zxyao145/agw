using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModifyTableName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_agentflow_node_execution_trace",
                table: "agentflow_node_execution_trace");

            migrationBuilder.RenameTable(
                name: "agentflow_node_execution_trace",
                newName: "agentflow_trace");

            migrationBuilder.RenameIndex(
                name: "ix_agentflow_node_execution_trace_project_id_context_id_task_id_start_time_utc",
                table: "agentflow_trace",
                newName: "ix_agentflow_trace_project_id_context_id_task_id_start_time_utc");

            migrationBuilder.RenameIndex(
                name: "ix_agentflow_node_execution_trace_agentflow_id_node_id_start_time_utc",
                table: "agentflow_trace",
                newName: "ix_agentflow_trace_agentflow_id_node_id_start_time_utc");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agentflow_trace",
                table: "agentflow_trace",
                column: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_agentflow_trace",
                table: "agentflow_trace");

            migrationBuilder.RenameTable(
                name: "agentflow_trace",
                newName: "agentflow_node_execution_trace");

            migrationBuilder.RenameIndex(
                name: "ix_agentflow_trace_project_id_context_id_task_id_start_time_utc",
                table: "agentflow_node_execution_trace",
                newName: "ix_agentflow_node_execution_trace_project_id_context_id_task_id_start_time_utc");

            migrationBuilder.RenameIndex(
                name: "ix_agentflow_trace_agentflow_id_node_id_start_time_utc",
                table: "agentflow_node_execution_trace",
                newName: "ix_agentflow_node_execution_trace_agentflow_id_node_id_start_time_utc");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agentflow_node_execution_trace",
                table: "agentflow_node_execution_trace",
                column: "id");
        }
    }
}
