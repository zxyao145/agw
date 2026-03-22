using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    content_path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_skills", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_skill_relations",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    skill_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_skill_relations", x => new { x.agent_id, x.skill_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_skill_relations_skill_id",
                table: "agent_skill_relations",
                column: "skill_id");

            migrationBuilder.CreateIndex(
                name: "ix_skills_name",
                table: "skills",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_skill_relations");

            migrationBuilder.DropTable(
                name: "skills");
        }
    }
}
