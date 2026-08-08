using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agent_file_memory_project_id_agent_id_path",
                table: "agent_file_memory");

            migrationBuilder.AddColumn<Guid>(
                name: "conversation_id",
                table: "agent_file_memory",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_agent_file_memory_project_id_conversation_id_agent_id_path",
                table: "agent_file_memory",
                columns: new[] { "project_id", "conversation_id", "agent_id", "path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agent_file_memory_project_id_conversation_id_agent_id_path",
                table: "agent_file_memory");

            migrationBuilder.DropColumn(
                name: "conversation_id",
                table: "agent_file_memory");

            migrationBuilder.CreateIndex(
                name: "ix_agent_file_memory_project_id_agent_id_path",
                table: "agent_file_memory",
                columns: new[] { "project_id", "agent_id", "path" },
                unique: true);
        }
    }
}
