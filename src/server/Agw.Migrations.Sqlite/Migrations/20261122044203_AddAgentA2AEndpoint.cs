using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentA2AEndpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_a2a_endpoint",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    card_url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    credentials = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'{}'"),
                    interface_url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    protocol_binding = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    interface_revision = table.Column<Guid>(type: "TEXT", nullable: false),
                    streaming = table.Column<bool>(type: "INTEGER", nullable: false),
                    extra = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'{}'"),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_a2a_endpoint", x => x.agent_id);
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "agent_a2a_endpoint");
        }
    }
}
