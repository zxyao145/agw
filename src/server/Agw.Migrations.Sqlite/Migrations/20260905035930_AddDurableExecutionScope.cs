using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableExecutionScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_conversation_id",
                table: "durable_execution",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "durable_execution",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "scope_backfilled",
                table: "durable_execution",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_scope_backfilled_user_id_id",
                table: "durable_execution",
                columns: new[] { "scope_backfilled", "user_id", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_user_id_project_id_project_conversation_id_status",
                table: "durable_execution",
                columns: new[] { "user_id", "project_id", "project_conversation_id", "status" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_durable_execution_scope_backfilled_user_id_id",
                table: "durable_execution"
            );

            migrationBuilder.DropIndex(
                name: "ix_durable_execution_user_id_project_id_project_conversation_id_status",
                table: "durable_execution"
            );

            migrationBuilder.DropColumn(name: "project_conversation_id", table: "durable_execution");

            migrationBuilder.DropColumn(name: "project_id", table: "durable_execution");

            migrationBuilder.DropColumn(name: "scope_backfilled", table: "durable_execution");
        }
    }
}
