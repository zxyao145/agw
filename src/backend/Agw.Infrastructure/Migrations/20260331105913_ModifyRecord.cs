using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModifyRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_task_records_context_id_conversation_sequence",
                table: "task_records");

            migrationBuilder.DropIndex(
                name: "ix_task_records_context_id_create_time",
                table: "task_records");

            migrationBuilder.DropIndex(
                name: "ix_task_records_context_id_session_id_create_time",
                table: "task_records");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_project_tasks_context_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "context_id",
                table: "task_records");

            migrationBuilder.CreateIndex(
                name: "ix_task_records_session_id_conversation_sequence",
                table: "task_records",
                columns: new[] { "session_id", "conversation_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_task_records_session_id_create_time",
                table: "task_records",
                columns: new[] { "session_id", "create_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_task_records_session_id_conversation_sequence",
                table: "task_records");

            migrationBuilder.DropIndex(
                name: "ix_task_records_session_id_create_time",
                table: "task_records");

            migrationBuilder.AddColumn<string>(
                name: "context_id",
                table: "task_records",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_project_tasks_context_id",
                table: "project_tasks",
                column: "context_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_records_context_id_conversation_sequence",
                table: "task_records",
                columns: new[] { "context_id", "conversation_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_task_records_context_id_create_time",
                table: "task_records",
                columns: new[] { "context_id", "create_time" });

            migrationBuilder.CreateIndex(
                name: "ix_task_records_context_id_session_id_create_time",
                table: "task_records",
                columns: new[] { "context_id", "session_id", "create_time" });
        }
    }
}
