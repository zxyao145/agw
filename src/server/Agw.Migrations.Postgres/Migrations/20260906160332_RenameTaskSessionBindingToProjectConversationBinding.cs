using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RenameTaskSessionBindingToProjectConversationBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "task_session_binding", newName: "project_conversation_binding");

            migrationBuilder.RenameIndex(
                name: "ix_task_session_binding_project_conversation_id_agent_id_exter",
                table: "project_conversation_binding",
                newName: "ix_project_conversation_binding_project_conversation_id_agent_"
            );

            migrationBuilder.RenameIndex(
                name: "ix_task_session_binding_external_agent_name_provider_session_id",
                table: "project_conversation_binding",
                newName: "ix_project_conversation_binding_external_agent_name_provider_s"
            );

            migrationBuilder.Sql(
                "ALTER TABLE project_conversation_binding RENAME CONSTRAINT pk_task_session_binding TO pk_project_conversation_binding;"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "project_conversation_binding", newName: "task_session_binding");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_binding_project_conversation_id_agent_",
                table: "task_session_binding",
                newName: "ix_task_session_binding_project_conversation_id_agent_id_exter"
            );

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_binding_external_agent_name_provider_s",
                table: "task_session_binding",
                newName: "ix_task_session_binding_external_agent_name_provider_session_id"
            );

            migrationBuilder.Sql(
                "ALTER TABLE task_session_binding RENAME CONSTRAINT pk_project_conversation_binding TO pk_task_session_binding;"
            );
        }
    }
}
