using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributedExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "durable_execution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    manifest_json = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    segment_index = table.Column<int>(type: "INTEGER", nullable: false),
                    checkpoint_json = table.Column<string>(type: "TEXT", nullable: true),
                    pending_interactions_json = table.Column<string>(type: "TEXT", nullable: true),
                    responses_json = table.Column<string>(type: "TEXT", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", nullable: true),
                    state_changed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    state_version = table.Column<Guid>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_durable_execution", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "execution_stream_entry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    execution_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    segment_index = table.Column<int>(type: "INTEGER", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_stream_entry", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_status_state_changed_at",
                table: "durable_execution",
                columns: new[] { "status", "state_changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_execution_stream_entry_execution_id_segment_index_sequence",
                table: "execution_stream_entry",
                columns: new[] { "execution_id", "segment_index", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "durable_execution");

            migrationBuilder.DropTable(
                name: "execution_stream_entry");
        }
    }
}
