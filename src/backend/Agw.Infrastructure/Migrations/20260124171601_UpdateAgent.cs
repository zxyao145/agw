using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "extra",
                table: "agents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "type",
                table: "agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "extra",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "type",
                table: "agents");
        }
    }
}
