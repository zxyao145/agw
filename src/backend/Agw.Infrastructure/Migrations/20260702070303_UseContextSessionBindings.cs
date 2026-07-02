using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UseContextSessionBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_task_session_binding_task_id_agent_id_external_agent_name",
                table: "task_session_binding");

            migrationBuilder.AddColumn<Guid>(
                name: "project_context_id",
                table: "task_session_binding",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE task_session_binding
                SET project_context_id = (
                    SELECT project_task_record.project_context_id
                    FROM project_task_record
                    WHERE project_task_record.task_id = task_session_binding.task_id
                    ORDER BY project_task_record.create_time DESC, project_task_record.id DESC
                    LIMIT 1
                );
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM task_session_binding
                WHERE project_context_id IS NULL;
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM task_session_binding
                WHERE id IN (
                    SELECT id
                    FROM (
                        SELECT
                            id,
                            ROW_NUMBER() OVER (
                                PARTITION BY project_context_id, agent_id, external_agent_name
                                ORDER BY COALESCE(update_time, create_time) DESC, create_time DESC, id DESC
                            ) AS duplicate_rank
                        FROM task_session_binding
                    ) ranked_bindings
                    WHERE duplicate_rank > 1
                );
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "project_context_id",
                table: "task_session_binding",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "task_id",
                table: "task_session_binding");

            migrationBuilder.CreateIndex(
                name: "ix_task_session_binding_project_context_id_agent_id_external_agent_name",
                table: "task_session_binding",
                columns: new[] { "project_context_id", "agent_id", "external_agent_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_task_record_project_context_id_conversation_sequence",
                table: "project_task_record",
                columns: new[] { "project_context_id", "conversation_sequence" });

            migrationBuilder.AddForeignKey(
                name: "fk_task_session_binding_project_context_project_context_id",
                table: "task_session_binding",
                column: "project_context_id",
                principalTable: "project_context",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_task_session_binding_project_context_project_context_id",
                table: "task_session_binding");

            migrationBuilder.DropIndex(
                name: "ix_task_session_binding_project_context_id_agent_id_external_agent_name",
                table: "task_session_binding");

            migrationBuilder.DropIndex(
                name: "ix_project_task_record_project_context_id_conversation_sequence",
                table: "project_task_record");

            migrationBuilder.AddColumn<Guid>(
                name: "task_id",
                table: "task_session_binding",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE task_session_binding
                SET task_id = (
                    SELECT project_task_record.task_id
                    FROM project_task_record
                    WHERE project_task_record.project_context_id = task_session_binding.project_context_id
                    ORDER BY project_task_record.create_time DESC, project_task_record.id DESC
                    LIMIT 1
                );
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM task_session_binding
                WHERE task_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "task_id",
                table: "task_session_binding",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "project_context_id",
                table: "task_session_binding");

            migrationBuilder.CreateIndex(
                name: "ix_task_session_binding_task_id_agent_id_external_agent_name",
                table: "task_session_binding",
                columns: new[] { "task_id", "agent_id", "external_agent_name" },
                unique: true);
        }
    }
}
