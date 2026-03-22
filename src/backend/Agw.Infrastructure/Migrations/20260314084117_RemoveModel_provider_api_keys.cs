using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveModel_provider_api_keys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_provider_api_keys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_provider_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    model_provider_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    api_key = table.Column<string>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    enable = table.Column<bool>(type: "INTEGER", nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_api_keys", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_api_keys_model_provider_id",
                table: "model_provider_api_keys",
                column: "model_provider_id");
        }
    }
}
