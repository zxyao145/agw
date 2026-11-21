using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
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
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    turn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    input_json = table.Column<string>(type: "text", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: true),
                    return_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    update_by = table.Column<string>(type: "text", nullable: true),
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
