using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProjectAndAgentflowEnable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enable",
                table: "project");

            migrationBuilder.DropColumn(
                name: "enable",
                table: "agentflow");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var booleanType = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? "boolean"
                : "INTEGER";

            migrationBuilder.AddColumn<bool>(
                name: "enable",
                table: "project",
                type: booleanType,
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "enable",
                table: "agentflow",
                type: booleanType,
                nullable: false,
                defaultValue: true);
        }
    }
}
