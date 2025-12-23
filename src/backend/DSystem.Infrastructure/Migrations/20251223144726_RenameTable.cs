using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agentflows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    system_prompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    pattern = table.Column<int>(type: "INTEGER", nullable: false),
                    configuration_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    type = table.Column<int>(type: "INTEGER", nullable: false),
                    max_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_models", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_providers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_leases",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    locked_by = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    locked_until_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_leases", x => x.project_id);
                    table.ForeignKey(
                        name: "fk_project_leases_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    input = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    output_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    started_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    finished_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_tasks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_providers",
                columns: table => new
                {
                    model_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    input_price = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    output_price = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    cache_read = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    cache_write = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    rps_limit = table.Column<int>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_providers", x => new { x.model_id, x.provider_id });
                    table.ForeignKey(
                        name: "fk_model_providers_models_model_id",
                        column: x => x.model_id,
                        principalTable: "models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_model_providers_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_provider_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    model_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    api_key = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_model_provider_api_keys_model_providers_model_id_provider_id",
                        columns: x => new { x.model_id, x.provider_id },
                        principalTable: "model_providers",
                        principalColumns: new[] { "model_id", "provider_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    instructions = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    system_prompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    model_provider_api_key_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                    table.ForeignKey(
                        name: "fk_agents_model_provider_api_keys_model_provider_api_key_id",
                        column: x => x.model_provider_api_key_id,
                        principalTable: "model_provider_api_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agentflow_nodes",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_id = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<int>(type: "INTEGER", nullable: false),
                    relate_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_nodes", x => new { x.agentflow_id, x.node_id });
                    table.ForeignKey(
                        name: "fk_agentflow_nodes_agentflows_agentflow_id",
                        column: x => x.agentflow_id,
                        principalTable: "Agentflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agentflow_nodes_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "agentflow_edges",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    edge_id = table.Column<string>(type: "TEXT", nullable: false),
                    source_node_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_node_id = table.Column<string>(type: "TEXT", nullable: false),
                    animated = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_edges", x => new { x.agentflow_id, x.edge_id });
                    table.ForeignKey(
                        name: "fk_agentflow_edges_agentflow_nodes_agentflow_id_source_node_id",
                        columns: x => new { x.agentflow_id, x.source_node_id },
                        principalTable: "agentflow_nodes",
                        principalColumns: new[] { "agentflow_id", "node_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agentflow_edges_agentflow_nodes_agentflow_id_target_node_id",
                        columns: x => new { x.agentflow_id, x.target_node_id },
                        principalTable: "agentflow_nodes",
                        principalColumns: new[] { "agentflow_id", "node_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agentflow_edges_agentflows_agentflow_id",
                        column: x => x.agentflow_id,
                        principalTable: "Agentflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edges_agentflow_id_source_node_id",
                table: "agentflow_edges",
                columns: new[] { "agentflow_id", "source_node_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edges_agentflow_id_target_node_id",
                table: "agentflow_edges",
                columns: new[] { "agentflow_id", "target_node_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_nodes_agent_id",
                table: "agentflow_nodes",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_nodes_agentflow_id_type_relate_id",
                table: "agentflow_nodes",
                columns: new[] { "agentflow_id", "type", "relate_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_model_provider_api_key_id",
                table: "agents",
                column: "model_provider_api_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_api_keys_model_id_provider_id",
                table: "model_provider_api_keys",
                columns: new[] { "model_id", "provider_id" });

            migrationBuilder.CreateIndex(
                name: "ix_model_providers_provider_id",
                table: "model_providers",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_leases_locked_until_utc",
                table: "project_leases",
                column: "locked_until_utc");

            migrationBuilder.CreateIndex(
                name: "ix_project_tasks_project_id_status_update_time",
                table: "project_tasks",
                columns: new[] { "project_id", "status", "update_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agentflow_edges");

            migrationBuilder.DropTable(
                name: "project_leases");

            migrationBuilder.DropTable(
                name: "project_tasks");

            migrationBuilder.DropTable(
                name: "agentflow_nodes");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "Agentflows");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "model_provider_api_keys");

            migrationBuilder.DropTable(
                name: "model_providers");

            migrationBuilder.DropTable(
                name: "models");

            migrationBuilder.DropTable(
                name: "providers");
        }
    }
}
