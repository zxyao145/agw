using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddModelCompactionLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "max_tokens",
                table: "model",
                newName: "max_context_window_tokens");

            migrationBuilder.AlterColumn<int>(
                name: "max_context_window_tokens",
                table: "model",
                type: "integer",
                nullable: false,
                defaultValue: 256000,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "max_output_tokens",
                table: "model",
                type: "integer",
                nullable: false,
                defaultValue: 64000);

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_token_limits",
                table: "model",
                sql: "max_context_window_tokens > 0 AND max_output_tokens > 0 AND max_output_tokens < max_context_window_tokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_model_token_limits",
                table: "model");

            migrationBuilder.DropColumn(
                name: "max_output_tokens",
                table: "model");

            migrationBuilder.AlterColumn<int>(
                name: "max_context_window_tokens",
                table: "model",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 256000);

            migrationBuilder.RenameColumn(
                name: "max_context_window_tokens",
                table: "model",
                newName: "max_tokens");
        }
    }
}
