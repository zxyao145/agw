using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModifySessionRecordTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_agent_sessions",
                table: "agent_sessions");

            migrationBuilder.RenameTable(
                name: "agent_sessions",
                newName: "agent_session_records");

            migrationBuilder.RenameIndex(
                name: "ix_agent_sessions_project_id_session_id",
                table: "agent_session_records",
                newName: "ix_agent_session_records_project_id_session_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agent_session_records",
                table: "agent_session_records",
                column: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_agent_session_records",
                table: "agent_session_records");

            migrationBuilder.RenameTable(
                name: "agent_session_records",
                newName: "agent_sessions");

            migrationBuilder.RenameIndex(
                name: "ix_agent_session_records_project_id_session_id",
                table: "agent_sessions",
                newName: "ix_agent_sessions_project_id_session_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agent_sessions",
                table: "agent_sessions",
                column: "id");
        }
    }
}
