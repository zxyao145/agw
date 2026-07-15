using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModifyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_project_task_context_id",
                table: "project_task");

            migrationBuilder.CreateIndex(
                name: "ix_project_task_context_id",
                table: "project_task",
                column: "context_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_project_task_context_id",
                table: "project_task");

            migrationBuilder.CreateIndex(
                name: "ix_project_task_context_id",
                table: "project_task",
                column: "context_id",
                unique: true);
        }
    }
}
