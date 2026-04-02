using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModifyRecordWithTaskId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "session_id",
                table: "task_records",
                newName: "task_id");

            migrationBuilder.RenameIndex(
                name: "ix_task_records_session_id_create_time",
                table: "task_records",
                newName: "ix_task_records_task_id_create_time");

            migrationBuilder.RenameIndex(
                name: "ix_task_records_session_id_conversation_sequence",
                table: "task_records",
                newName: "ix_task_records_task_id_conversation_sequence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "task_id",
                table: "task_records",
                newName: "session_id");

            migrationBuilder.RenameIndex(
                name: "ix_task_records_task_id_create_time",
                table: "task_records",
                newName: "ix_task_records_session_id_create_time");

            migrationBuilder.RenameIndex(
                name: "ix_task_records_task_id_conversation_sequence",
                table: "task_records",
                newName: "ix_task_records_session_id_conversation_sequence");
        }
    }
}
