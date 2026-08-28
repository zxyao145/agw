using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUserDataIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE agentflow SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE api_token SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE integration_connection SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql("UPDATE job SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';");
            migrationBuilder.Sql(
                "UPDATE mcp_server SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE model SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE plugin_installation SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE project SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE provider SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE skill SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE project_conversation SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE model_provider_relation SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE agent SET create_by = '1001' WHERE create_by IS NULL OR btrim(create_by) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE durable_execution SET user_id = '1001' WHERE user_id IS NULL OR btrim(user_id) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE agentflow_checkpoint SET user_id = '1001' WHERE user_id IS NULL OR btrim(user_id) = '';"
            );
            migrationBuilder.Sql(
                "UPDATE user_memory SET user_id = '1001' WHERE user_id IS NULL OR btrim(user_id) = '';"
            );

            migrationBuilder.DropIndex(name: "ix_skill_name", table: "skill");

            migrationBuilder.DropIndex(name: "ix_provider_name_provider_type", table: "provider");

            migrationBuilder.DropIndex(name: "ix_project_name", table: "project");

            migrationBuilder.DropIndex(name: "ix_plugin_installation_plugin_id", table: "plugin_installation");

            migrationBuilder.DropIndex(name: "ix_model_name", table: "model");

            migrationBuilder.DropIndex(name: "ix_api_token_normalized_name", table: "api_token");

            migrationBuilder.DropIndex(name: "ix_agent_name", table: "agent");

            migrationBuilder.AlterTable(
                name: "plugin_installation",
                comment: "Stores per-user plugin installation setup.",
                oldComment: "Stores platform-wide plugin installation configuration."
            );

            migrationBuilder.AlterColumn<string>(
                name: "create_by",
                table: "plugin_installation",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true
            );

            migrationBuilder.AlterColumn<string>(
                name: "create_by",
                table: "job",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "user_id",
                table: "agent_usage",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true
            );

            migrationBuilder.Sql(
                "UPDATE agent_usage SET user_id = (SELECT create_by FROM project WHERE project.id = agent_usage.project_id) "
                    + "WHERE (user_id IS NULL OR btrim(user_id) = '') "
                    + "AND EXISTS (SELECT 1 FROM project WHERE project.id = agent_usage.project_id AND btrim(create_by) <> '');"
            );
            migrationBuilder.Sql(
                "UPDATE agent_usage SET user_id = '1001' WHERE user_id IS NULL OR btrim(user_id) = '';"
            );

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "agent_usage",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_skill_create_by_name",
                table: "skill",
                columns: new[] { "create_by", "name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_provider_create_by_name_provider_type",
                table: "provider",
                columns: new[] { "create_by", "name", "provider_type" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_create_by_name",
                table: "project",
                columns: new[] { "create_by", "name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_plugin_installation_create_by_plugin_id",
                table: "plugin_installation",
                columns: new[] { "create_by", "plugin_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_model_create_by_name",
                table: "model",
                columns: new[] { "create_by", "name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_api_token_create_by_normalized_name",
                table: "api_token",
                columns: new[] { "create_by", "normalized_name" },
                unique: true
            );

            migrationBuilder.CreateIndex(name: "ix_agent_usage_user_id", table: "agent_usage", column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_create_by_name",
                table: "agent",
                columns: new[] { "create_by", "name" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_skill_create_by_name", table: "skill");

            migrationBuilder.DropIndex(name: "ix_provider_create_by_name_provider_type", table: "provider");

            migrationBuilder.DropIndex(name: "ix_project_create_by_name", table: "project");

            migrationBuilder.DropIndex(
                name: "ix_plugin_installation_create_by_plugin_id",
                table: "plugin_installation"
            );

            migrationBuilder.DropIndex(name: "ix_model_create_by_name", table: "model");

            migrationBuilder.DropIndex(name: "ix_api_token_create_by_normalized_name", table: "api_token");

            migrationBuilder.DropIndex(name: "ix_agent_usage_user_id", table: "agent_usage");

            migrationBuilder.DropIndex(name: "ix_agent_create_by_name", table: "agent");

            migrationBuilder.DropColumn(name: "user_id", table: "agent_usage");

            migrationBuilder.AlterTable(
                name: "plugin_installation",
                comment: "Stores platform-wide plugin installation configuration.",
                oldComment: "Stores per-user plugin installation setup."
            );

            migrationBuilder.AlterColumn<string>(
                name: "create_by",
                table: "plugin_installation",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text"
            );

            migrationBuilder.AlterColumn<string>(
                name: "create_by",
                table: "job",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text"
            );

            migrationBuilder.CreateIndex(name: "ix_skill_name", table: "skill", column: "name", unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_name_provider_type",
                table: "provider",
                columns: new[] { "name", "provider_type" },
                unique: true
            );

            migrationBuilder.CreateIndex(name: "ix_project_name", table: "project", column: "name", unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plugin_installation_plugin_id",
                table: "plugin_installation",
                column: "plugin_id",
                unique: true
            );

            migrationBuilder.CreateIndex(name: "ix_model_name", table: "model", column: "name", unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_token_normalized_name",
                table: "api_token",
                column: "normalized_name",
                unique: true
            );

            migrationBuilder.CreateIndex(name: "ix_agent_name", table: "agent", column: "name", unique: true);
        }
    }
}
