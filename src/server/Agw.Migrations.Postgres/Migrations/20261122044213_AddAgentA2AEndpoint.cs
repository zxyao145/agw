using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
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
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    credentials = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'{}'"),
                    interface_url = table.Column<string>(
                        type: "character varying(2048)",
                        maxLength: 2048,
                        nullable: false
                    ),
                    protocol_binding = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    interface_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    streaming = table.Column<bool>(type: "boolean", nullable: false),
                    extra = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'{}'"),
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
