using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceFileMemoryWithProjectMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "agent"
                SET "tools" = replace(
                    "tools",
                    '"name":"file-memory"',
                    '"name":"project-memory"')
                WHERE "tools" LIKE '%"name":"file-memory"%';
                """);
            migrationBuilder.Sql(
                """
                UPDATE "project"
                SET "tools" = replace(
                    "tools",
                    '"name":"file-memory"',
                    '"name":"project-memory"')
                WHERE "tools" LIKE '%"name":"file-memory"%';
                """);

            migrationBuilder.DropTable(
                name: "agent_file_memory");

            migrationBuilder.CreateTable(
                name: "project_memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_memory", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_memory_project_id_path",
                table: "project_memory",
                columns: new[] { "project_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_memory_updated_at",
                table: "project_memory",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "agent"
                SET "tools" = replace(
                    "tools",
                    '"name":"project-memory"',
                    '"name":"file-memory"')
                WHERE "tools" LIKE '%"name":"project-memory"%';
                """);
            migrationBuilder.Sql(
                """
                UPDATE "project"
                SET "tools" = replace(
                    "tools",
                    '"name":"project-memory"',
                    '"name":"file-memory"')
                WHERE "tools" LIKE '%"name":"project-memory"%';
                """);

            migrationBuilder.DropTable(
                name: "project_memory");

            migrationBuilder.CreateTable(
                name: "agent_file_memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_file_memory", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_file_memory_project_id_conversation_id_agent_id_path",
                table: "agent_file_memory",
                columns: new[] { "project_id", "conversation_id", "agent_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_file_memory_updated_at",
                table: "agent_file_memory",
                column: "updated_at");
        }
    }
}
