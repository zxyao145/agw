using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RenameTaskSessionBindingToProjectConversationBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Retain SQLite's primary-key constraint name to avoid a table rebuild that adds EF relationship foreign keys.
            migrationBuilder.RenameTable(name: "task_session_binding", newName: "project_conversation_binding");

            migrationBuilder.RenameIndex(
                name: "ix_task_session_binding_project_conversation_id_agent_id_external_agent_name",
                table: "project_conversation_binding",
                newName: "ix_project_conversation_binding_project_conversation_id_agent_id_external_agent_name"
            );

            migrationBuilder.RenameIndex(
                name: "ix_task_session_binding_external_agent_name_provider_session_id",
                table: "project_conversation_binding",
                newName: "ix_project_conversation_binding_external_agent_name_provider_session_id"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "project_conversation_binding", newName: "task_session_binding");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_binding_project_conversation_id_agent_id_external_agent_name",
                table: "task_session_binding",
                newName: "ix_task_session_binding_project_conversation_id_agent_id_external_agent_name"
            );

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_binding_external_agent_name_provider_session_id",
                table: "task_session_binding",
                newName: "ix_task_session_binding_external_agent_name_provider_session_id"
            );
        }
    }
}
