using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeparateAgentUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "usage_cached_input_token_count",
                table: "project_context");

            migrationBuilder.DropColumn(
                name: "usage_input_token_count",
                table: "project_context");

            migrationBuilder.DropColumn(
                name: "usage_output_token_count",
                table: "project_context");

            migrationBuilder.DropColumn(
                name: "usage_reasoning_token_count",
                table: "project_context");

            migrationBuilder.DropColumn(
                name: "usage_total_token_count",
                table: "project_context");

            migrationBuilder.CreateTable(
                name: "agent_usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    input_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                    output_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                    total_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                    cached_input_token_count = table.Column<long>(type: "INTEGER", nullable: false),
                    reasoning_token_count = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_usage", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_usage_agent_name",
                table: "agent_usage",
                column: "agent_name");

            migrationBuilder.CreateIndex(
                name: "ix_agent_usage_project_id_context_id",
                table: "agent_usage",
                columns: new[] { "project_id", "context_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_usage_recorded_at",
                table: "agent_usage",
                column: "recorded_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_usage");

            migrationBuilder.AddColumn<long>(
                name: "usage_cached_input_token_count",
                table: "project_context",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "usage_input_token_count",
                table: "project_context",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "usage_output_token_count",
                table: "project_context",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "usage_reasoning_token_count",
                table: "project_context",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "usage_total_token_count",
                table: "project_context",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
