using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReInit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentflow",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    system_prompt = table.Column<string>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("pk_agentflow", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_type = table.Column<int>(type: "INTEGER", nullable: true),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    prompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    trigger_type = table.Column<int>(type: "INTEGER", nullable: false),
                    trigger_value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    next_run_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    retry_count = table.Column<int>(type: "INTEGER", nullable: false),
                    max_retry_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    row_version = table.Column<byte[]>(type: "BLOB", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    start_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    end_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    success = table.Column<bool>(type: "INTEGER", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_server",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    transport_type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    command = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    arguments = table.Column<string>(type: "TEXT", nullable: false),
                    working_directory = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    environment_variables = table.Column<string>(type: "TEXT", nullable: false),
                    url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    headers = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_server", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model",
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
                    table.PrimaryKey("pk_model", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    access_token = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    refresh_token = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    token_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    scope = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_authorization", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    type = table.Column<int>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    workspace = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    extra_setting = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_task_record",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", maxLength: 64, nullable: false),
                    agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    conversation_sequence = table.Column<long>(type: "INTEGER", nullable: true),
                    conversation_payload = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_task_record", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    provider_type = table.Column<int>(type: "INTEGER", nullable: false),
                    endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "skill",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    content_path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_skill", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: "Untitled"),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    finished_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_task", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_provider_relation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("pk_model_provider_relation", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_auth_config",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    auth_type = table.Column<int>(type: "INTEGER", nullable: false),
                    api_key = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    env_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_auth_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    system_prompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    model_provider_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    tools = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    type = table.Column<int>(type: "INTEGER", nullable: false),
                    extra = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent", x => x.id);
                });

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

            migrationBuilder.CreateTable(
                name: "agent_skill_relations",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    skill_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_skill_relations", x => new { x.agent_id, x.skill_id });
                });

            migrationBuilder.CreateTable(
                name: "agentflow_node",
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
                    table.PrimaryKey("pk_agentflow_node", x => new { x.agentflow_id, x.node_id });
                });

            migrationBuilder.CreateTable(
                name: "agentflow_edge",
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
                    table.PrimaryKey("pk_agentflow_edge", x => new { x.agentflow_id, x.edge_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_model_provider_id",
                table: "agent",
                column: "model_provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_name",
                table: "agent",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_mcp_tool_servers_mcp_tool_server_id",
                table: "agent_mcp_tool_servers",
                column: "mcp_tool_server_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_skill_relations_skill_id",
                table: "agent_skill_relations",
                column: "skill_id");

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_source_node_id",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "source_node_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_target_node_id",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "target_node_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agent_id",
                table: "agentflow_node",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agentflow_id_type_relate_id",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "type", "relate_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_next_run_time",
                table: "job",
                columns: new[] { "is_enabled", "status", "next_run_time" });

            migrationBuilder.CreateIndex(
                name: "ix_task_project",
                table: "job",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_log_task_id_start_time",
                table: "job_log",
                columns: new[] { "task_id", "start_time" });

            migrationBuilder.CreateIndex(
                name: "ix_model_name",
                table: "model",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_relation_model_id",
                table: "model_provider_relation",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_relation_provider_id",
                table: "model_provider_relation",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_expires_at_utc",
                table: "oauth_authorization",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_provider",
                table: "oauth_authorization",
                column: "provider");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_provider_subject",
                table: "oauth_authorization",
                columns: new[] { "provider", "subject" });

            migrationBuilder.CreateIndex(
                name: "ix_project_name",
                table: "project",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_task_context_id",
                table: "project_task",
                column: "context_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_task_project_id",
                table: "project_task",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_task_project_id_status_update_time",
                table: "project_task",
                columns: new[] { "project_id", "status", "update_time" });

            migrationBuilder.CreateIndex(
                name: "ix_project_task_record_task_id_conversation_sequence",
                table: "project_task_record",
                columns: new[] { "task_id", "conversation_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_project_task_record_task_id_create_time",
                table: "project_task_record",
                columns: new[] { "task_id", "create_time" });

            migrationBuilder.CreateIndex(
                name: "ix_provider_name_provider_type",
                table: "provider",
                columns: new[] { "name", "provider_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_auth_config_provider_id",
                table: "provider_auth_config",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_skill_name",
                table: "skill",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_mcp_tool_servers");

            migrationBuilder.DropTable(
                name: "agent_skill_relations");

            migrationBuilder.DropTable(
                name: "agentflow_edge");

            migrationBuilder.DropTable(
                name: "job");

            migrationBuilder.DropTable(
                name: "job_log");

            migrationBuilder.DropTable(
                name: "oauth_authorization");

            migrationBuilder.DropTable(
                name: "project_task");

            migrationBuilder.DropTable(
                name: "project_task_record");

            migrationBuilder.DropTable(
                name: "provider_auth_config");

            migrationBuilder.DropTable(
                name: "mcp_server");

            migrationBuilder.DropTable(
                name: "skill");

            migrationBuilder.DropTable(
                name: "agentflow_node");

            migrationBuilder.DropTable(
                name: "project");

            migrationBuilder.DropTable(
                name: "agent");

            migrationBuilder.DropTable(
                name: "agentflow");

            migrationBuilder.DropTable(
                name: "model_provider_relation");

            migrationBuilder.DropTable(
                name: "model");

            migrationBuilder.DropTable(
                name: "provider");
        }
    }
}
