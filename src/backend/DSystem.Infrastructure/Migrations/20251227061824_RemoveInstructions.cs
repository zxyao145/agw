using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveInstructions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "instructions",
                table: "agents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "instructions",
                table: "agents",
                type: "TEXT",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");
        }
    }
}
