using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_auth_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    auth_type = table.Column<int>(type: "INTEGER", nullable: false),
                    api_key = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    env_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_auth_configs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_auth_configs_provider_id",
                table: "provider_auth_configs",
                column: "provider_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_auth_configs");
        }
    }
}
