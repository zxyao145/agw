using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionSteer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "execution_steer",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    turn_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    input_json = table.Column<string>(type: "TEXT", nullable: false),
                    step_index = table.Column<int>(type: "INTEGER", nullable: true),
                    return_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_steer", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_execution_steer_turn_id_status",
                table: "execution_steer",
                columns: new[] { "turn_id", "status" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "execution_steer");
        }
    }
}
