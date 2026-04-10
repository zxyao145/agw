using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateJobLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "task_id",
                table: "job_log",
                newName: "job_id");

            migrationBuilder.RenameIndex(
                name: "ix_job_log_task_id_start_time",
                table: "job_log",
                newName: "ix_job_log_job_id_start_time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "job_id",
                table: "job_log",
                newName: "task_id");

            migrationBuilder.RenameIndex(
                name: "ix_job_log_job_id_start_time",
                table: "job_log",
                newName: "ix_job_log_task_id_start_time");
        }
    }
}
