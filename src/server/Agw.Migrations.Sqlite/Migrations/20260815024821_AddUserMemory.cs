using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_memory", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_memory_user_id_normalized_name",
                table: "user_memory",
                columns: new[] { "user_id", "normalized_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_memory");
        }
    }
}
