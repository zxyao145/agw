using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionBind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_task_session_binding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    external_agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    provider_session_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_task_session_binding", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_task_session_binding_external_agent_name_provider_session_id",
                table: "project_task_session_binding",
                columns: new[] { "external_agent_name", "provider_session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_task_session_binding_task_id_agent_id_external_agent_name",
                table: "project_task_session_binding",
                columns: new[] { "task_id", "agent_id", "external_agent_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_task_session_binding");
        }
    }
}
