using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModifyJobLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_task_execution_logs",
                table: "task_execution_logs");

            migrationBuilder.RenameTable(
                name: "task_execution_logs",
                newName: "job_logs");

            migrationBuilder.RenameIndex(
                name: "ix_task_execution_logs_task_id_start_time",
                table: "job_logs",
                newName: "ix_job_logs_task_id_start_time");

            migrationBuilder.AddPrimaryKey(
                name: "pk_job_logs",
                table: "job_logs",
                column: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_job_logs",
                table: "job_logs");

            migrationBuilder.RenameTable(
                name: "job_logs",
                newName: "task_execution_logs");

            migrationBuilder.RenameIndex(
                name: "ix_job_logs_task_id_start_time",
                table: "task_execution_logs",
                newName: "ix_task_execution_logs_task_id_start_time");

            migrationBuilder.AddPrimaryKey(
                name: "pk_task_execution_logs",
                table: "task_execution_logs",
                column: "id");
        }
    }
}
