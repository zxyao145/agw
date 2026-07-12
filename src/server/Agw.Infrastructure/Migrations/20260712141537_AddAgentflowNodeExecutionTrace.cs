using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentflowNodeExecutionTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentflow_node_execution_trace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    start_time_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    node_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    node_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    input = table.Column<string>(type: "text", nullable: false),
                    duration_milliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_node_execution_trace", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_execution_trace_agentflow_id_node_id_start_time_utc",
                table: "agentflow_node_execution_trace",
                columns: new[] { "agentflow_id", "node_id", "start_time_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_execution_trace_project_id_context_id_task_id_start_time_utc",
                table: "agentflow_node_execution_trace",
                columns: new[] { "project_id", "context_id", "task_id", "start_time_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agentflow_node_execution_trace");
        }
    }
}
