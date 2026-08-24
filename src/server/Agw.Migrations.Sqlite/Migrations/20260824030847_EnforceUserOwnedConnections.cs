using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUserOwnedConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_integration_connection_alias", table: "integration_connection");

            migrationBuilder.Sql("UPDATE integration_connection SET create_by = '1001' WHERE create_by IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "create_by",
                table: "integration_connection",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_create_by_alias",
                table: "integration_connection",
                columns: new[] { "create_by", "alias" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_integration_connection_create_by_alias",
                table: "integration_connection"
            );

            migrationBuilder.AlterColumn<string>(
                name: "create_by",
                table: "integration_connection",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT"
            );

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_alias",
                table: "integration_connection",
                column: "alias",
                unique: true
            );
        }
    }
}
