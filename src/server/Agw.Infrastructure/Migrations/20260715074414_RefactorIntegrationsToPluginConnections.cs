using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefactorIntegrationsToPluginConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isPostgres = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";
            var guidType = isPostgres ? "uuid" : "TEXT";
            var stringType = isPostgres ? "text" : "TEXT";
            var boolType = isPostgres ? "boolean" : "INTEGER";
            var integerType = isPostgres ? "integer" : "INTEGER";
            var dateTimeOffsetType = isPostgres ? "timestamp with time zone" : "TEXT";

            migrationBuilder.DropTable(
                name: "agent_app_relation");

            migrationBuilder.DropTable(
                name: "oauth_authorization");

            migrationBuilder.DropTable(
                name: "project_app_relation");

            migrationBuilder.DropTable(
                name: "app_instance");

            migrationBuilder.CreateTable(
                name: "integration_connection",
                columns: table => new
                {
                    id = table.Column<Guid>(type: guidType, nullable: false),
                    plugin_id = table.Column<string>(type: stringType, maxLength: 128, nullable: false),
                    connector_id = table.Column<string>(type: stringType, maxLength: 128, nullable: false),
                    auth_scheme_id = table.Column<string>(type: stringType, maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: stringType, maxLength: 200, nullable: false),
                    alias = table.Column<string>(type: stringType, maxLength: 128, nullable: false),
                    configuration_json = table.Column<string>(type: stringType, maxLength: 16000, nullable: false),
                    enabled = table.Column<bool>(type: boolType, nullable: false),
                    status = table.Column<string>(type: stringType, maxLength: 64, nullable: false),
                    subject = table.Column<string>(type: stringType, maxLength: 500, nullable: true),
                    last_validated_at_utc = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: true),
                    last_validation_error_code = table.Column<string>(type: stringType, maxLength: 128, nullable: true),
                    validation_metadata_json = table.Column<string>(type: stringType, maxLength: 8000, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: false),
                    create_by = table.Column<string>(type: stringType, nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: true),
                    update_by = table.Column<string>(type: stringType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_connection", x => x.id);
                },
                comment: "Represents an external account or service endpoint available to agents.");

            migrationBuilder.CreateTable(
                name: "plugin_installation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: guidType, nullable: false),
                    plugin_id = table.Column<string>(type: stringType, maxLength: 128, nullable: false),
                    enabled = table.Column<bool>(type: boolType, nullable: false),
                    configuration_json = table.Column<string>(type: stringType, maxLength: 16000, nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: false),
                    create_by = table.Column<string>(type: stringType, nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: true),
                    update_by = table.Column<string>(type: stringType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plugin_installation", x => x.id);
                },
                comment: "Stores platform-wide plugin installation configuration.");

            migrationBuilder.CreateTable(
                name: "agent_connection_relation",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: guidType, nullable: false),
                    connection_id = table.Column<Guid>(type: guidType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_connection_relation", x => new { x.agent_id, x.connection_id });
                },
                comment: "Binds an agent to an integration connection.");

            migrationBuilder.CreateTable(
                name: "integration_connection_credential",
                columns: table => new
                {
                    id = table.Column<Guid>(type: guidType, nullable: false),
                    connection_id = table.Column<Guid>(type: guidType, nullable: false),
                    slot = table.Column<string>(type: stringType, maxLength: 512, nullable: false),
                    source = table.Column<string>(type: stringType, maxLength: 32, nullable: false),
                    protected_value = table.Column<string>(type: stringType, maxLength: 16000, nullable: true),
                    environment_variable_name = table.Column<string>(type: stringType, maxLength: 512, nullable: true),
                    display_hint = table.Column<string>(type: stringType, maxLength: 200, nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: true),
                    metadata_json = table.Column<string>(type: stringType, maxLength: 8000, nullable: true),
                    format_version = table.Column<int>(type: integerType, nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: false),
                    create_by = table.Column<string>(type: stringType, nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: true),
                    update_by = table.Column<string>(type: stringType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_connection_credential", x => x.id);
                },
                comment: "Stores protected credentials owned by an integration connection.");

            migrationBuilder.CreateTable(
                name: "project_connection_relation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: guidType, nullable: false),
                    connection_id = table.Column<Guid>(type: guidType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_connection_relation", x => new { x.project_id, x.connection_id });
                },
                comment: "Binds a project to an integration connection.");

            migrationBuilder.CreateTable(
                name: "plugin_installation_credential",
                columns: table => new
                {
                    id = table.Column<Guid>(type: guidType, nullable: false),
                    plugin_installation_id = table.Column<Guid>(type: guidType, nullable: false),
                    slot = table.Column<string>(type: stringType, maxLength: 512, nullable: false),
                    source = table.Column<string>(type: stringType, maxLength: 32, nullable: false),
                    protected_value = table.Column<string>(type: stringType, maxLength: 16000, nullable: true),
                    environment_variable_name = table.Column<string>(type: stringType, maxLength: 512, nullable: true),
                    display_hint = table.Column<string>(type: stringType, maxLength: 200, nullable: true),
                    format_version = table.Column<int>(type: integerType, nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: false),
                    create_by = table.Column<string>(type: stringType, nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: true),
                    update_by = table.Column<string>(type: stringType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plugin_installation_credential", x => x.id);
                },
                comment: "Stores protected credentials owned by a plugin installation.");

            migrationBuilder.CreateIndex(
                name: "ix_agent_connection_relation_connection_id",
                table: "agent_connection_relation",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_alias",
                table: "integration_connection",
                column: "alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_plugin_id",
                table: "integration_connection",
                column: "plugin_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_status",
                table: "integration_connection",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_credential_connection_id_slot",
                table: "integration_connection_credential",
                columns: new[] { "connection_id", "slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_credential_expires_at_utc",
                table: "integration_connection_credential",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_plugin_installation_plugin_id",
                table: "plugin_installation",
                column: "plugin_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plugin_installation_credential_plugin_installation_id_slot",
                table: "plugin_installation_credential",
                columns: new[] { "plugin_installation_id", "slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_connection_relation_connection_id",
                table: "project_connection_relation",
                column: "connection_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var isPostgres = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";
            var guidType = isPostgres ? "uuid" : "TEXT";
            var stringType = isPostgres ? "text" : "TEXT";
            var boolType = isPostgres ? "boolean" : "INTEGER";
            var dateTimeOffsetType = isPostgres ? "timestamp with time zone" : "TEXT";

            migrationBuilder.DropTable(
                name: "agent_connection_relation");

            migrationBuilder.DropTable(
                name: "integration_connection_credential");

            migrationBuilder.DropTable(
                name: "plugin_installation_credential");

            migrationBuilder.DropTable(
                name: "project_connection_relation");

            migrationBuilder.DropTable(
                name: "plugin_installation");

            migrationBuilder.DropTable(
                name: "integration_connection");

            migrationBuilder.CreateTable(
                name: "app_instance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: guidType, nullable: false),
                    app_name = table.Column<string>(type: stringType, maxLength: 128, nullable: false),
                    client_id = table.Column<string>(type: stringType, maxLength: 200, nullable: false),
                    client_secret = table.Column<string>(type: stringType, maxLength: 2000, nullable: false),
                    create_by = table.Column<string>(type: stringType, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: false),
                    update_by = table.Column<string>(type: stringType, nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: true),
                    use_pkce = table.Column<bool>(type: boolType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_instance", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_app_relation",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: guidType, nullable: false),
                    app_instance_id = table.Column<Guid>(type: guidType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_app_relation", x => new { x.agent_id, x.app_instance_id });
                });

            migrationBuilder.CreateTable(
                name: "oauth_authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: guidType, nullable: false),
                    access_token = table.Column<string>(type: stringType, maxLength: 4000, nullable: false),
                    app_instance_id = table.Column<Guid>(type: guidType, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: dateTimeOffsetType, nullable: true),
                    refresh_token = table.Column<string>(type: stringType, maxLength: 4000, nullable: true),
                    subject = table.Column<string>(type: stringType, maxLength: 200, nullable: false),
                    token_type = table.Column<string>(type: stringType, maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_authorization", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_app_relation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: guidType, nullable: false),
                    app_instance_id = table.Column<Guid>(type: guidType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_app_relation", x => new { x.project_id, x.app_instance_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_app_relation_app_instance_id",
                table: "agent_app_relation",
                column: "app_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_app_instance_app_name",
                table: "app_instance",
                column: "app_name");

            migrationBuilder.CreateIndex(
                name: "ix_app_instance_client_id",
                table: "app_instance",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_app_instance_id",
                table: "oauth_authorization",
                column: "app_instance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_expires_at_utc",
                table: "oauth_authorization",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_project_app_relation_app_instance_id",
                table: "project_app_relation",
                column: "app_instance_id");
        }
    }
}
