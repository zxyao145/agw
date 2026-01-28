using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingForProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "extra_setting",
                table: "projects",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "extra_setting",
                table: "projects");
        }
    }
}
