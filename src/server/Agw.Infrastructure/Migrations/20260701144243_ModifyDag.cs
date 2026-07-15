using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModifyDag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agentflow_node_agent_id",
                table: "agentflow_node");

            migrationBuilder.DropIndex(
                name: "ix_agentflow_node_agentflow_id_type_relate_id",
                table: "agentflow_node");

            migrationBuilder.DropColumn(
                name: "agent_id",
                table: "agentflow_node");

            migrationBuilder.DropColumn(
                name: "configuration_json",
                table: "agentflow");

            migrationBuilder.DropColumn(
                name: "pattern",
                table: "agentflow");

            migrationBuilder.RenameColumn(
                name: "type",
                table: "agentflow_node",
                newName: "kind");

            migrationBuilder.RenameColumn(
                name: "animated",
                table: "agentflow_edge",
                newName: "kind");

            migrationBuilder.AlterColumn<Guid>(
                name: "relate_id",
                table: "agentflow_node",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "config_json",
                table: "agentflow_node",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "instructions",
                table: "agentflow_node",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "agentflow_node",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "position_json",
                table: "agentflow_node",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "condition_json",
                table: "agentflow_edge",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "config_json",
                table: "agentflow_edge",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "agentflow_edge",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agentflow_id_kind_relate_id",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "kind", "relate_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agentflow_node_agentflow_id_kind_relate_id",
                table: "agentflow_node");

            migrationBuilder.DropColumn(
                name: "config_json",
                table: "agentflow_node");

            migrationBuilder.DropColumn(
                name: "instructions",
                table: "agentflow_node");

            migrationBuilder.DropColumn(
                name: "name",
                table: "agentflow_node");

            migrationBuilder.DropColumn(
                name: "position_json",
                table: "agentflow_node");

            migrationBuilder.DropColumn(
                name: "condition_json",
                table: "agentflow_edge");

            migrationBuilder.DropColumn(
                name: "config_json",
                table: "agentflow_edge");

            migrationBuilder.DropColumn(
                name: "label",
                table: "agentflow_edge");

            migrationBuilder.RenameColumn(
                name: "kind",
                table: "agentflow_node",
                newName: "type");

            migrationBuilder.RenameColumn(
                name: "kind",
                table: "agentflow_edge",
                newName: "animated");

            migrationBuilder.AlterColumn<Guid>(
                name: "relate_id",
                table: "agentflow_node",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                table: "agentflow_node",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "configuration_json",
                table: "agentflow",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pattern",
                table: "agentflow",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agent_id",
                table: "agentflow_node",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agentflow_id_type_relate_id",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "type", "relate_id" },
                unique: true);
        }
    }
}
