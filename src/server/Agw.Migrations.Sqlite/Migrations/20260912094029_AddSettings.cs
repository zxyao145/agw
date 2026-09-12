using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "setting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    value_json = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setting", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_setting_key",
                table: "setting",
                column: "key",
                unique: true,
                filter: "user_id IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_setting_user_id_key",
                table: "setting",
                columns: new[] { "user_id", "key" },
                unique: true,
                filter: "user_id IS NOT NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "setting");
        }
    }
}
