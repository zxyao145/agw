using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRecordTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "agent_session_records",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "title",
                table: "agent_session_records");
        }
    }
}
