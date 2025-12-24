using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProviderIdKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agents_model_providers_model_provider_id",
                table: "agents");

            migrationBuilder.DropTable(
                name: "model_provider_api_key");

            migrationBuilder.RenameColumn(
                name: "model_provider_id",
                table: "agents",
                newName: "model_provider_api_key_id");

            migrationBuilder.RenameIndex(
                name: "ix_agents_model_provider_id",
                table: "agents",
                newName: "ix_agents_model_provider_api_key_id");

            migrationBuilder.CreateTable(
                name: "model_provider_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    model_provider_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    api_key = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_model_provider_api_keys_model_providers_model_provider_id",
                        column: x => x.model_provider_id,
                        principalTable: "model_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_api_keys_model_provider_id",
                table: "model_provider_api_keys",
                column: "model_provider_id");

            migrationBuilder.AddForeignKey(
                name: "fk_agents_model_provider_api_keys_model_provider_api_key_id",
                table: "agents",
                column: "model_provider_api_key_id",
                principalTable: "model_provider_api_keys",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agents_model_provider_api_keys_model_provider_api_key_id",
                table: "agents");

            migrationBuilder.DropTable(
                name: "model_provider_api_keys");

            migrationBuilder.RenameColumn(
                name: "model_provider_api_key_id",
                table: "agents",
                newName: "model_provider_id");

            migrationBuilder.RenameIndex(
                name: "ix_agents_model_provider_api_key_id",
                table: "agents",
                newName: "ix_agents_model_provider_id");

            migrationBuilder.CreateTable(
                name: "model_provider_api_key",
                columns: table => new
                {
                    model_provider_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    api_key = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_api_key", x => new { x.model_provider_id, x.id });
                    table.ForeignKey(
                        name: "fk_model_provider_api_key_model_providers_model_provider_id",
                        column: x => x.model_provider_id,
                        principalTable: "model_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddForeignKey(
                name: "fk_agents_model_providers_model_provider_id",
                table: "agents",
                column: "model_provider_id",
                principalTable: "model_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
