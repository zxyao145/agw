using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTaskUnuseRecordField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "create_by",
                table: "task_records");

            migrationBuilder.DropColumn(
                name: "input",
                table: "task_records");

            migrationBuilder.DropColumn(
                name: "messages",
                table: "task_records");

            migrationBuilder.DropColumn(
                name: "update_by",
                table: "task_records");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "create_by",
                table: "task_records",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "input",
                table: "task_records",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "messages",
                table: "task_records",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_by",
                table: "task_records",
                type: "TEXT",
                nullable: true);
        }
    }
}
