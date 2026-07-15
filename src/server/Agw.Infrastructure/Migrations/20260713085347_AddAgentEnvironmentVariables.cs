using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentEnvironmentVariables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "environment_variables",
                table: "agent",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "environment_variables",
                table: "agent");
        }
    }
}
