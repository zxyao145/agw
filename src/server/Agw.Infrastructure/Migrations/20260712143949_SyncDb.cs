using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SyncDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
        }
    }
}
