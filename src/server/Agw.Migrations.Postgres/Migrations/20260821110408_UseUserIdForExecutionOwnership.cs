using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class UseUserIdForExecutionOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: "user_name", table: "durable_execution", newName: "user_id");

            migrationBuilder.RenameColumn(name: "user_name", table: "agentflow_checkpoint", newName: "user_id");

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "durable_execution",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256
            );

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "agentflow_checkpoint",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256
            );

            migrationBuilder.Sql("UPDATE durable_execution SET user_id = '1001';");
            migrationBuilder.Sql("UPDATE agentflow_checkpoint SET user_id = '1001';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE durable_execution SET user_id = 'admin';");
            migrationBuilder.Sql("UPDATE agentflow_checkpoint SET user_id = 'admin';");

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "durable_execution",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128
            );

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "agentflow_checkpoint",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128
            );

            migrationBuilder.RenameColumn(name: "user_id", table: "durable_execution", newName: "user_name");

            migrationBuilder.RenameColumn(name: "user_id", table: "agentflow_checkpoint", newName: "user_name");
        }
    }
}
