using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "summary_model_provider_id",
                table: "agentflow",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "enable_summary",
                table: "agent",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "summary_model_provider_id",
                table: "agentflow");

            migrationBuilder.DropColumn(
                name: "enable_summary",
                table: "agent");
        }
    }
}
