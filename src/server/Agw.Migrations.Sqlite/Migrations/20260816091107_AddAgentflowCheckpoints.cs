using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentflowCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentflow_checkpoint",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_execution_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    context_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    is_durable = table.Column<bool>(type: "INTEGER", nullable: false),
                    boundary_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    definition_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    markers_json = table.Column<string>(type: "TEXT", nullable: false),
                    checkpoint_json = table.Column<string>(type: "TEXT", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_checkpoint", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_checkpoint_project_conversation_id_agentflow_id_boundary_sequence",
                table: "agentflow_checkpoint",
                columns: new[] { "project_conversation_id", "agentflow_id", "boundary_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_checkpoint_source_execution_id",
                table: "agentflow_checkpoint",
                column: "source_execution_id"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "agentflow_checkpoint");
        }
    }
}
