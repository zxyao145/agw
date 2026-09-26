using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ReInit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    input_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                    output_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                    total_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                    cached_input_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                    reasoning_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_usage", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "agentflow",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    system_prompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    summary_model_provider_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "agentflow_trace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    start_time_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
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
                    error = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_trace", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "api_token",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    prefix = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    secret_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_token", x => x.id);
                },
                comment: "Stores hashed API tokens used by external Agw clients."
            );

            migrationBuilder.CreateTable(
                name: "auth_desktop_login_grant",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_challenge = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false),
                    session_version = table.Column<int>(type: "INTEGER", nullable: false),
                    expires_at_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_desktop_login_grant", x => x.code_hash);
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_external_identity",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    issuer = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_external_identity", x => x.user_id);
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_user",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    session_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_user", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_user_id_sequence",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    next_id = table.Column<long>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_user_id_sequence", x => x.id);
                    table.CheckConstraint("ck_auth_user_id_sequence_singleton", "id = 1");
                    table.CheckConstraint("ck_auth_user_id_sequence_start", "next_id >= 10000");
                }
            );

            migrationBuilder.CreateTable(
                name: "durable_execution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    project_conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    scope_backfilled = table.Column<bool>(type: "INTEGER", nullable: false),
                    manifest_json = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    segment_index = table.Column<int>(type: "INTEGER", nullable: false),
                    checkpoint_json = table.Column<string>(type: "TEXT", nullable: true),
                    pending_interactions_json = table.Column<string>(type: "TEXT", nullable: true),
                    responses_json = table.Column<string>(type: "TEXT", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", nullable: true),
                    state_changed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    state_version = table.Column<Guid>(type: "TEXT", nullable: false),
                    worker_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    lease_epoch = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    lease_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_event_sequence = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    turn_checkpoint_json = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_durable_execution", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "execution_stream_entry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    turn_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    turn_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    segment_index = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_stream_entry", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "integration_connection",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    plugin_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    connector_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    auth_scheme_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    alias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    configuration_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    subject = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    last_validated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    last_validation_error_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    validation_metadata_json = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_connection", x => x.id);
                },
                comment: "Represents an external account or service endpoint available to agents."
            );

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
                    active_execution_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    active_attempt_started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    row_version = table.Column<byte[]>(type: "BLOB", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job", x => x.id);
                    table.CheckConstraint(
                        "ck_job_active_attempt",
                        "(status = 2 AND active_execution_id IS NOT NULL AND active_attempt_started_at IS NOT NULL) OR (status <> 2 AND active_execution_id IS NULL AND active_attempt_started_at IS NULL)"
                    );
                }
            );

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
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_server", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "model",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    max_context_window_tokens = table.Column<int>(
                        type: "INTEGER",
                        nullable: false,
                        defaultValue: 256000
                    ),
                    max_output_tokens = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 64000),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model", x => x.id);
                    table.CheckConstraint(
                        "ck_model_token_limits",
                        "max_context_window_tokens > 0 AND max_output_tokens > 0 AND max_output_tokens < max_context_window_tokens"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "plugin_installation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    plugin_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    configuration_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plugin_installation", x => x.id);
                },
                comment: "Stores per-user plugin installation setup."
            );

            migrationBuilder.CreateTable(
                name: "project",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    type = table.Column<int>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    workspace = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    additional_directories = table.Column<string>(
                        type: "TEXT",
                        nullable: false,
                        defaultValueSql: "'[]'"
                    ),
                    extra_setting = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    tools = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    environment_variables = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "project_memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_memory", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "provider",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    provider_type = table.Column<int>(type: "INTEGER", nullable: false),
                    endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "setting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    value_json = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setting", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "skill",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    content_path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    remote_url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_skill", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "user_memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_memory", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "agentflow_node",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    relate_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    position_json = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    instructions = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    config_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_node", x => new { x.agentflow_id, x.node_id });
                }
            );

            migrationBuilder.CreateTable(
                name: "integration_connection_credential",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    connection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    slot = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    protected_value = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    metadata_json = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    format_version = table.Column<int>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_connection_credential", x => x.id);
                },
                comment: "Stores protected credentials owned by an integration connection."
            );

            migrationBuilder.CreateTable(
                name: "job_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    start_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    end_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    success = table.Column<bool>(type: "INTEGER", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_log", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "plugin_installation_credential",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    plugin_installation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    slot = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    protected_value = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    format_version = table.Column<int>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plugin_installation_credential", x => x.id);
                },
                comment: "Stores protected credentials owned by a plugin installation."
            );

            migrationBuilder.CreateTable(
                name: "project_connection_relation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    connection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_connection_relation", x => new { x.project_id, x.connection_id });
                },
                comment: "Binds a project to an integration connection."
            );

            migrationBuilder.CreateTable(
                name: "project_conversation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    generation = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    title = table.Column<string>(
                        type: "TEXT",
                        maxLength: 200,
                        nullable: false,
                        defaultValue: "Untitled"
                    ),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_conversation", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "project_mcp_server_relation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    mcp_tool_server_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_mcp_server_relation", x => new { x.project_id, x.mcp_tool_server_id });
                }
            );

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
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_relation", x => x.id);
                }
            );

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
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_auth_config", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "project_skill_relation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    skill_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_skill_relation", x => new { x.project_id, x.skill_id });
                }
            );

            migrationBuilder.CreateTable(
                name: "remote_skill_cache",
                columns: table => new
                {
                    skill_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    content_json = table.Column<string>(type: "TEXT", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_skill_cache", x => x.skill_id);
                }
            );

            migrationBuilder.CreateTable(
                name: "agentflow_edge",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    edge_id = table.Column<string>(type: "TEXT", nullable: false),
                    source_node_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_node_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    condition_json = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    config_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_edge", x => new { x.agentflow_id, x.edge_id });
                }
            );

            migrationBuilder.CreateTable(
                name: "agentflow_checkpoint",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_execution_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    is_durable = table.Column<bool>(type: "INTEGER", nullable: false),
                    boundary_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    definition_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    markers_json = table.Column<string>(type: "TEXT", nullable: false),
                    checkpoint_json = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_checkpoint", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "project_conversation_binding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    external_agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    provider_session_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    seen_through_sequence = table.Column<long>(type: "INTEGER", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_conversation_binding", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "project_conversation_chat_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    finished_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    task_error_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    conversation_sequence = table.Column<long>(type: "INTEGER", nullable: true),
                    turn_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    step_index = table.Column<int>(type: "INTEGER", nullable: true),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    history_scope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    purpose = table.Column<string>(
                        type: "TEXT",
                        maxLength: 16,
                        nullable: false,
                        defaultValue: "message"
                    ),
                    conversation_payload = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_conversation_chat_history", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "project_conversation_turn",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    runtime_type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    input_message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    first_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: true),
                    step_count = table.Column<int>(type: "INTEGER", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_conversation_turn", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    system_prompt = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    model_provider_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    enable_summary = table.Column<bool>(type: "INTEGER", nullable: false),
                    summary_model_provider_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    type = table.Column<int>(type: "INTEGER", nullable: false),
                    external_agent_kind = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    extra = table.Column<string>(type: "TEXT", nullable: true),
                    response_schema = table.Column<string>(type: "TEXT", nullable: true),
                    tools = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    environment_variables = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "agent_connection_relation",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    connection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_connection_relation", x => new { x.agent_id, x.connection_id });
                },
                comment: "Binds an agent to an integration connection."
            );

            migrationBuilder.CreateTable(
                name: "agent_mcp_server_relation",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    mcp_tool_server_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_mcp_server_relation", x => new { x.agent_id, x.mcp_tool_server_id });
                }
            );

            migrationBuilder.CreateTable(
                name: "agent_session_state",
                columns: table => new
                {
                    project_conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_node_id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    serialized_session = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_agent_session_state",
                        x => new
                        {
                            x.project_conversation_id,
                            x.agent_id,
                            x.agentflow_node_id,
                        }
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "agent_skill_relation",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    skill_id = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_skill_relation", x => new { x.agent_id, x.skill_id });
                }
            );

            migrationBuilder.InsertData(
                table: "auth_user",
                columns: new[]
                {
                    "id",
                    "create_by",
                    "create_time",
                    "display_name",
                    "email",
                    "session_version",
                    "update_by",
                    "update_time",
                },
                values: new object[]
                {
                    1001L,
                    "1001",
                    new DateTimeOffset(
                        new DateTime(2026, 9, 17, 0, 0, 0, 0, DateTimeKind.Unspecified),
                        new TimeSpan(0, 0, 0, 0, 0)
                    ),
                    "admin",
                    null,
                    1,
                    null,
                    null,
                }
            );

            migrationBuilder.InsertData(
                table: "auth_user_id_sequence",
                columns: new[] { "id", "next_id" },
                values: new object[] { 1, 10000L }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agent_create_by_name",
                table: "agent",
                columns: new[] { "create_by", "name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_agent_model_provider_id",
                table: "agent",
                column: "model_provider_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_agent_connection_relation_connection_id",
                table: "agent_connection_relation",
                column: "connection_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_agent_mcp_server_relation_mcp_tool_server_id",
                table: "agent_mcp_server_relation",
                column: "mcp_tool_server_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_state_agent_id",
                table: "agent_session_state",
                column: "agent_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_state_updated_at",
                table: "agent_session_state",
                column: "updated_at"
            );

            migrationBuilder.CreateIndex(
                name: "ix_agent_skill_relation_skill_id",
                table: "agent_skill_relation",
                column: "skill_id"
            );

            migrationBuilder.CreateIndex(name: "ix_agent_usage_agent_name", table: "agent_usage", column: "agent_name");

            migrationBuilder.CreateIndex(
                name: "ix_agent_usage_project_id_context_id",
                table: "agent_usage",
                columns: new[] { "project_id", "context_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agent_usage_recorded_at",
                table: "agent_usage",
                column: "recorded_at"
            );

            migrationBuilder.CreateIndex(name: "ix_agent_usage_user_id", table: "agent_usage", column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_checkpoint_project_conversation_id_agentflow_id_boundary_sequence",
                table: "agentflow_checkpoint",
                columns: new[] { "project_conversation_id", "agentflow_id", "boundary_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_checkpoint_source_execution_id",
                table: "agentflow_checkpoint",
                column: "source_execution_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_source_node_id",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "source_node_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_target_node_id",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "target_node_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agentflow_id_kind_relate_id",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "kind", "relate_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_trace_agentflow_id_node_id_start_time_utc",
                table: "agentflow_trace",
                columns: new[] { "agentflow_id", "node_id", "start_time_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_trace_project_id_context_id_task_id_start_time_utc",
                table: "agentflow_trace",
                columns: new[] { "project_id", "context_id", "task_id", "start_time_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_api_token_create_by_normalized_name",
                table: "api_token",
                columns: new[] { "create_by", "normalized_name" },
                unique: true
            );

            migrationBuilder.CreateIndex(name: "ix_api_token_prefix", table: "api_token", column: "prefix");

            migrationBuilder.CreateIndex(
                name: "ix_auth_desktop_login_grant_expires_at_ms",
                table: "auth_desktop_login_grant",
                column: "expires_at_ms"
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_external_identity_issuer_subject",
                table: "auth_external_identity",
                columns: new[] { "issuer", "subject" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_scope_backfilled_user_id_id",
                table: "durable_execution",
                columns: new[] { "scope_backfilled", "user_id", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_status_lease_expires_at",
                table: "durable_execution",
                columns: new[] { "status", "lease_expires_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_status_state_changed_at",
                table: "durable_execution",
                columns: new[] { "status", "state_changed_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_user_id_project_id_project_conversation_id_status",
                table: "durable_execution",
                columns: new[] { "user_id", "project_id", "project_conversation_id", "status" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_execution_stream_entry_turn_id_turn_sequence",
                table: "execution_stream_entry",
                columns: new[] { "turn_id", "turn_sequence" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_create_by_alias",
                table: "integration_connection",
                columns: new[] { "create_by", "alias" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_plugin_id",
                table: "integration_connection",
                column: "plugin_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_status",
                table: "integration_connection",
                column: "status"
            );

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_credential_connection_id_slot",
                table: "integration_connection_credential",
                columns: new[] { "connection_id", "slot" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_credential_expires_at_utc",
                table: "integration_connection_credential",
                column: "expires_at_utc"
            );

            migrationBuilder.CreateIndex(
                name: "ix_job_active_execution_id",
                table: "job",
                column: "active_execution_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_task_next_run_time",
                table: "job",
                columns: new[] { "is_enabled", "status", "next_run_time" }
            );

            migrationBuilder.CreateIndex(name: "ix_task_project", table: "job", column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_log_job_id_start_time",
                table: "job_log",
                columns: new[] { "job_id", "start_time" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_model_create_by_name",
                table: "model",
                columns: new[] { "create_by", "name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_relation_model_id",
                table: "model_provider_relation",
                column: "model_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_relation_provider_id",
                table: "model_provider_relation",
                column: "provider_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_plugin_installation_create_by_plugin_id",
                table: "plugin_installation",
                columns: new[] { "create_by", "plugin_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_plugin_installation_credential_plugin_installation_id_slot",
                table: "plugin_installation_credential",
                columns: new[] { "plugin_installation_id", "slot" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_create_by_name",
                table: "project",
                columns: new[] { "create_by", "name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_connection_relation_connection_id",
                table: "project_connection_relation",
                column: "connection_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_job_id",
                table: "project_conversation",
                column: "job_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_project_id",
                table: "project_conversation",
                column: "project_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_project_id_context_id",
                table: "project_conversation",
                columns: new[] { "project_id", "context_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_update_time",
                table: "project_conversation",
                column: "update_time"
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_binding_external_agent_name_provider_session_id",
                table: "project_conversation_binding",
                columns: new[] { "external_agent_name", "provider_session_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_binding_project_conversation_id_agent_id_external_agent_name",
                table: "project_conversation_binding",
                columns: new[] { "project_conversation_id", "agent_id", "external_agent_name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id",
                table: "project_conversation_chat_history",
                column: "project_conversation_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_conversation_sequence",
                table: "project_conversation_chat_history",
                columns: new[] { "project_conversation_id", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_history_scope_conversation_sequence",
                table: "project_conversation_chat_history",
                columns: new[] { "project_conversation_id", "history_scope", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_purpose_conversation_sequence",
                table: "project_conversation_chat_history",
                columns: new[] { "project_conversation_id", "purpose", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_task_id_conversation_sequence",
                table: "project_conversation_chat_history",
                columns: new[] { "task_id", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_task_id_create_time",
                table: "project_conversation_chat_history",
                columns: new[] { "task_id", "create_time" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_turn_id_conversation_sequence",
                table: "project_conversation_chat_history",
                columns: new[] { "turn_id", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_turn_project_conversation_id_first_sequence",
                table: "project_conversation_turn",
                columns: new[] { "project_conversation_id", "first_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_turn_project_conversation_id_status",
                table: "project_conversation_turn",
                columns: new[] { "project_conversation_id", "status" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_turn_task_id",
                table: "project_conversation_turn",
                column: "task_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_mcp_server_relation_mcp_tool_server_id",
                table: "project_mcp_server_relation",
                column: "mcp_tool_server_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_memory_project_id_path",
                table: "project_memory",
                columns: new[] { "project_id", "path" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_memory_updated_at",
                table: "project_memory",
                column: "updated_at"
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_skill_relation_skill_id",
                table: "project_skill_relation",
                column: "skill_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_provider_create_by_name_provider_type",
                table: "provider",
                columns: new[] { "create_by", "name", "provider_type" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_provider_auth_config_provider_id",
                table: "provider_auth_config",
                column: "provider_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_setting_key",
                table: "setting",
                column: "key",
                unique: true,
                filter: "user_id IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_setting_user_id_key",
                table: "setting",
                columns: new[] { "user_id", "key" },
                unique: true,
                filter: "user_id IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_skill_create_by_name",
                table: "skill",
                columns: new[] { "create_by", "name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_user_memory_user_id_normalized_name",
                table: "user_memory",
                columns: new[] { "user_id", "normalized_name" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "agent_connection_relation");

            migrationBuilder.DropTable(name: "agent_mcp_server_relation");

            migrationBuilder.DropTable(name: "agent_session_state");

            migrationBuilder.DropTable(name: "agent_skill_relation");

            migrationBuilder.DropTable(name: "agent_usage");

            migrationBuilder.DropTable(name: "agentflow_checkpoint");

            migrationBuilder.DropTable(name: "agentflow_edge");

            migrationBuilder.DropTable(name: "agentflow_trace");

            migrationBuilder.DropTable(name: "api_token");

            migrationBuilder.DropTable(name: "auth_desktop_login_grant");

            migrationBuilder.DropTable(name: "auth_external_identity");

            migrationBuilder.DropTable(name: "auth_user");

            migrationBuilder.DropTable(name: "auth_user_id_sequence");

            migrationBuilder.DropTable(name: "durable_execution");

            migrationBuilder.DropTable(name: "execution_stream_entry");

            migrationBuilder.DropTable(name: "integration_connection_credential");

            migrationBuilder.DropTable(name: "job_log");

            migrationBuilder.DropTable(name: "plugin_installation_credential");

            migrationBuilder.DropTable(name: "project_connection_relation");

            migrationBuilder.DropTable(name: "project_conversation_binding");

            migrationBuilder.DropTable(name: "project_conversation_chat_history");

            migrationBuilder.DropTable(name: "project_conversation_turn");

            migrationBuilder.DropTable(name: "project_mcp_server_relation");

            migrationBuilder.DropTable(name: "project_memory");

            migrationBuilder.DropTable(name: "project_skill_relation");

            migrationBuilder.DropTable(name: "provider_auth_config");

            migrationBuilder.DropTable(name: "remote_skill_cache");

            migrationBuilder.DropTable(name: "setting");

            migrationBuilder.DropTable(name: "user_memory");

            migrationBuilder.DropTable(name: "agent");

            migrationBuilder.DropTable(name: "agentflow_node");

            migrationBuilder.DropTable(name: "job");

            migrationBuilder.DropTable(name: "plugin_installation");

            migrationBuilder.DropTable(name: "integration_connection");

            migrationBuilder.DropTable(name: "project_conversation");

            migrationBuilder.DropTable(name: "mcp_server");

            migrationBuilder.DropTable(name: "skill");

            migrationBuilder.DropTable(name: "model_provider_relation");

            migrationBuilder.DropTable(name: "agentflow");

            migrationBuilder.DropTable(name: "project");

            migrationBuilder.DropTable(name: "model");

            migrationBuilder.DropTable(name: "provider");
        }
    }
}
