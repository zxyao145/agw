using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAgentModelProviderRelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "env_key",
                table: "provider_auth_configs",
                newName: "env_name");

            migrationBuilder.RenameColumn(
                name: "model_provider_api_key_id",
                table: "agents",
                newName: "model_provider_id");

            migrationBuilder.RenameIndex(
                name: "ix_agents_model_provider_api_key_id",
                table: "agents",
                newName: "ix_agents_model_provider_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "env_name",
                table: "provider_auth_configs",
                newName: "env_key");

            migrationBuilder.RenameColumn(
                name: "model_provider_id",
                table: "agents",
                newName: "model_provider_api_key_id");

            migrationBuilder.RenameIndex(
                name: "ix_agents_model_provider_id",
                table: "agents",
                newName: "ix_agents_model_provider_api_key_id");
        }
    }
}
