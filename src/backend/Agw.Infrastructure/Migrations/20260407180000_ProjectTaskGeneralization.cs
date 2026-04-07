using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProjectTaskGeneralization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_project_tasks_agent_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "agent_type",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "agent_id",
                table: "project_tasks");

            migrationBuilder.DropColumn(
                name: "description",
                table: "project_tasks");

            migrationBuilder.AddColumn<Guid>(
                name: "job_id",
                table: "project_tasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "job_id",
                table: "project_tasks");

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

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "project_tasks",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_project_tasks_agent_id",
                table: "project_tasks",
                column: "agent_id");
        }
    }
}
