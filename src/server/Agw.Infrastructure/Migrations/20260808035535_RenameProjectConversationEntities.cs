using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameProjectConversationEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isPostgres = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";

            migrationBuilder.RenameTable(
                name: "project_context",
                newName: "project_conversation");

            migrationBuilder.RenameTable(
                name: "project_task_record",
                newName: "project_conversation_chat_history");

            migrationBuilder.RenameColumn(
                name: "project_context_id",
                table: "project_conversation_chat_history",
                newName: "project_conversation_id");

            migrationBuilder.RenameColumn(
                name: "project_context_id",
                table: "task_session_binding",
                newName: "project_conversation_id");

            migrationBuilder.RenameColumn(
                name: "project_context_id",
                table: "agent_session_state",
                newName: "project_conversation_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_context_job_id",
                table: "project_conversation",
                newName: "ix_project_conversation_job_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_context_project_id",
                table: "project_conversation",
                newName: "ix_project_conversation_project_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_context_project_id_context_id",
                table: "project_conversation",
                newName: "ix_project_conversation_project_id_context_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_context_update_time",
                table: "project_conversation",
                newName: "ix_project_conversation_update_time");

            migrationBuilder.RenameIndex(
                name: "ix_project_task_record_project_context_id",
                table: "project_conversation_chat_history",
                newName: "ix_project_conversation_chat_history_project_conversation_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_task_record_project_context_id_conversation_sequence",
                table: "project_conversation_chat_history",
                newName: "ix_project_conversation_chat_history_project_conversation_id_conversation_sequence");

            migrationBuilder.RenameIndex(
                name: "ix_project_task_record_task_id_conversation_sequence",
                table: "project_conversation_chat_history",
                newName: "ix_project_conversation_chat_history_task_id_conversation_sequence");

            migrationBuilder.RenameIndex(
                name: "ix_project_task_record_task_id_create_time",
                table: "project_conversation_chat_history",
                newName: "ix_project_conversation_chat_history_task_id_create_time");

            migrationBuilder.RenameIndex(
                name: "ix_task_session_binding_project_context_id_agent_id_external_agent_name",
                table: "task_session_binding",
                newName: "ix_task_session_binding_project_conversation_id_agent_id_external_agent_name");

            if (isPostgres)
            {
                migrationBuilder.Sql(
                    "ALTER TABLE project_conversation "
                    + "RENAME CONSTRAINT pk_project_context TO pk_project_conversation;");
                migrationBuilder.Sql(
                    "ALTER TABLE project_conversation_chat_history "
                    + "RENAME CONSTRAINT pk_project_task_record TO pk_project_conversation_chat_history;");
                migrationBuilder.Sql(
                    "ALTER TABLE task_session_binding "
                    + "RENAME CONSTRAINT fk_task_session_binding_project_context_project_context_id "
                    + "TO fk_task_session_binding_project_conversation_project_conversation_id;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var isPostgres = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";

            migrationBuilder.RenameTable(
                name: "project_conversation",
                newName: "project_context");

            migrationBuilder.RenameTable(
                name: "project_conversation_chat_history",
                newName: "project_task_record");

            migrationBuilder.RenameColumn(
                name: "project_conversation_id",
                table: "project_task_record",
                newName: "project_context_id");

            migrationBuilder.RenameColumn(
                name: "project_conversation_id",
                table: "task_session_binding",
                newName: "project_context_id");

            migrationBuilder.RenameColumn(
                name: "project_conversation_id",
                table: "agent_session_state",
                newName: "project_context_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_job_id",
                table: "project_context",
                newName: "ix_project_context_job_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_project_id",
                table: "project_context",
                newName: "ix_project_context_project_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_project_id_context_id",
                table: "project_context",
                newName: "ix_project_context_project_id_context_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_update_time",
                table: "project_context",
                newName: "ix_project_context_update_time");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id",
                table: "project_task_record",
                newName: "ix_project_task_record_project_context_id");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_conversation_sequence",
                table: "project_task_record",
                newName: "ix_project_task_record_project_context_id_conversation_sequence");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_chat_history_task_id_conversation_sequence",
                table: "project_task_record",
                newName: "ix_project_task_record_task_id_conversation_sequence");

            migrationBuilder.RenameIndex(
                name: "ix_project_conversation_chat_history_task_id_create_time",
                table: "project_task_record",
                newName: "ix_project_task_record_task_id_create_time");

            migrationBuilder.RenameIndex(
                name: "ix_task_session_binding_project_conversation_id_agent_id_external_agent_name",
                table: "task_session_binding",
                newName: "ix_task_session_binding_project_context_id_agent_id_external_agent_name");

            if (isPostgres)
            {
                migrationBuilder.Sql(
                    "ALTER TABLE project_context "
                    + "RENAME CONSTRAINT pk_project_conversation TO pk_project_context;");
                migrationBuilder.Sql(
                    "ALTER TABLE project_task_record "
                    + "RENAME CONSTRAINT pk_project_conversation_chat_history TO pk_project_task_record;");
                migrationBuilder.Sql(
                    "ALTER TABLE task_session_binding "
                    + "RENAME CONSTRAINT fk_task_session_binding_project_conversation_project_conversation_id "
                    + "TO fk_task_session_binding_project_context_project_context_id;");
            }
        }
    }
}
