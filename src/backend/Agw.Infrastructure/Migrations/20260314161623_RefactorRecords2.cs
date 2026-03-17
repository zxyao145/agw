using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefactorRecords2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agent_id",
                table: "task_records");

            migrationBuilder.DropColumn(
                name: "agent_type",
                table: "task_records");

            migrationBuilder.AddColumn<string>(
                name: "agent_name",
                table: "task_records",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                table: "project_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "agent_type",
                table: "project_tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_project_tasks_agent_id",
                table: "project_tasks",
                column: "agent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_project_tasks_agent_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "agent_name",
                table: "task_records");

            migrationBuilder.DropColumn(
                name: "agent_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "agent_type",
                table: "project_tasks");

            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                table: "task_records",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "agent_type",
                table: "task_records",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
