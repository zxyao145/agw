using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentflowRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM agentflow_trace;");
            migrationBuilder.Sql("DELETE FROM agentflow_checkpoint;");
            migrationBuilder.Sql("DELETE FROM agentflow_edge;");
            migrationBuilder.Sql("DELETE FROM agentflow_node;");
            migrationBuilder.Sql("DELETE FROM agentflow;");

            migrationBuilder.DropPrimaryKey(name: "pk_agentflow_node", table: "agentflow_node");

            migrationBuilder.DropIndex(name: "ix_agentflow_node_agentflow_id_kind_relate_id", table: "agentflow_node");

            migrationBuilder.DropPrimaryKey(name: "pk_agentflow_edge", table: "agentflow_edge");

            migrationBuilder.DropIndex(name: "ix_agentflow_edge_agentflow_id_source_node_id", table: "agentflow_edge");

            migrationBuilder.DropIndex(name: "ix_agentflow_edge_agentflow_id_target_node_id", table: "agentflow_edge");

            migrationBuilder.DropColumn(name: "position_json", table: "agentflow_node");

            migrationBuilder.DropColumn(name: "config_json", table: "agentflow_edge");

            migrationBuilder.DropColumn(name: "definition_fingerprint", table: "agentflow_checkpoint");

            migrationBuilder.DropColumn(name: "summary_model_provider_id", table: "agentflow");

            migrationBuilder.RenameColumn(name: "kind", table: "agentflow_edge", newName: "switch_case_order");

            migrationBuilder.AddColumn<string>(
                name: "extra",
                table: "project_conversation_turn",
                type: "text",
                nullable: false,
                defaultValueSql: "'{}'"
            );

            migrationBuilder.AddColumn<string>(
                name: "extra",
                table: "agentflow_trace",
                type: "text",
                nullable: false,
                defaultValueSql: "'{}'"
            );

            migrationBuilder.AlterColumn<string>(
                name: "config_json",
                table: "agentflow_node",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: false,
                defaultValue: "{}",
                oldClrType: typeof(string),
                oldType: "character varying(16000)",
                oldMaxLength: 16000,
                oldNullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "agentflow_revision",
                table: "agentflow_node",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<double>(
                name: "position_x",
                table: "agentflow_node",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0
            );

            migrationBuilder.AddColumn<double>(
                name: "position_y",
                table: "agentflow_node",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0
            );

            migrationBuilder.AddColumn<int>(
                name: "routing_strategy",
                table: "agentflow_node",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<Guid>(
                name: "summary_model_provider_id",
                table: "agentflow_node",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AlterColumn<string>(
                name: "condition_json",
                table: "agentflow_edge",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "[]",
                oldClrType: typeof(string),
                oldType: "character varying(8000)",
                oldMaxLength: 8000,
                oldNullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "agentflow_revision",
                table: "agentflow_edge",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<string>(
                name: "choice_id",
                table: "agentflow_edge",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "is_fan_in_barrier",
                table: "agentflow_edge",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<bool>(
                name: "is_switch_default",
                table: "agentflow_edge",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<int>(
                name: "agentflow_revision",
                table: "agentflow_checkpoint",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<int>(
                name: "runtime_version",
                table: "agentflow_checkpoint",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<int>(
                name: "latest_revision",
                table: "agentflow",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<int>(
                name: "published_revision",
                table: "agentflow",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_agentflow_node",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "agentflow_revision", "node_id" }
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_agentflow_edge",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "agentflow_revision", "edge_id" }
            );

            migrationBuilder.CreateTable(
                name: "agentflow_block_member",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agentflow_revision = table.Column<int>(type: "integer", nullable: false),
                    node_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    member_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    relate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    instructions = table.Column<string>(
                        type: "character varying(8000)",
                        maxLength: 8000,
                        nullable: true
                    ),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_manager = table.Column<bool>(type: "boolean", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    update_by = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_agentflow_block_member",
                        x => new
                        {
                            x.agentflow_id,
                            x.agentflow_revision,
                            x.node_id,
                            x.member_id,
                        }
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "agentflow_revision",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    update_by = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agentflow_revision", x => new { x.agentflow_id, x.revision });
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agentflow_id_agentflow_revision_kind_relate_",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "agentflow_revision", "kind", "relate_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_agentflow_revision_source_node_",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "agentflow_revision", "source_node_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_agentflow_revision_target_node_",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "agentflow_revision", "target_node_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_block_member_agentflow_id_agentflow_revision_kind",
                table: "agentflow_block_member",
                columns: new[] { "agentflow_id", "agentflow_revision", "kind", "relate_id" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "agentflow_block_member");

            migrationBuilder.DropTable(name: "agentflow_revision");

            migrationBuilder.DropPrimaryKey(name: "pk_agentflow_node", table: "agentflow_node");

            migrationBuilder.DropIndex(
                name: "ix_agentflow_node_agentflow_id_agentflow_revision_kind_relate_",
                table: "agentflow_node"
            );

            migrationBuilder.DropPrimaryKey(name: "pk_agentflow_edge", table: "agentflow_edge");

            migrationBuilder.DropIndex(
                name: "ix_agentflow_edge_agentflow_id_agentflow_revision_source_node_",
                table: "agentflow_edge"
            );

            migrationBuilder.DropIndex(
                name: "ix_agentflow_edge_agentflow_id_agentflow_revision_target_node_",
                table: "agentflow_edge"
            );

            migrationBuilder.DropColumn(name: "extra", table: "project_conversation_turn");

            migrationBuilder.DropColumn(name: "extra", table: "agentflow_trace");

            migrationBuilder.DropColumn(name: "agentflow_revision", table: "agentflow_node");

            migrationBuilder.DropColumn(name: "position_x", table: "agentflow_node");

            migrationBuilder.DropColumn(name: "position_y", table: "agentflow_node");

            migrationBuilder.DropColumn(name: "routing_strategy", table: "agentflow_node");

            migrationBuilder.DropColumn(name: "summary_model_provider_id", table: "agentflow_node");

            migrationBuilder.DropColumn(name: "agentflow_revision", table: "agentflow_edge");

            migrationBuilder.DropColumn(name: "choice_id", table: "agentflow_edge");

            migrationBuilder.DropColumn(name: "is_fan_in_barrier", table: "agentflow_edge");

            migrationBuilder.DropColumn(name: "is_switch_default", table: "agentflow_edge");

            migrationBuilder.DropColumn(name: "agentflow_revision", table: "agentflow_checkpoint");

            migrationBuilder.DropColumn(name: "runtime_version", table: "agentflow_checkpoint");

            migrationBuilder.DropColumn(name: "latest_revision", table: "agentflow");

            migrationBuilder.DropColumn(name: "published_revision", table: "agentflow");

            migrationBuilder.RenameColumn(name: "switch_case_order", table: "agentflow_edge", newName: "kind");

            migrationBuilder.AlterColumn<string>(
                name: "config_json",
                table: "agentflow_node",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16000)",
                oldMaxLength: 16000,
                oldDefaultValue: "{}"
            );

            migrationBuilder.AddColumn<string>(
                name: "position_json",
                table: "agentflow_node",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true
            );

            migrationBuilder.AlterColumn<string>(
                name: "condition_json",
                table: "agentflow_edge",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(8000)",
                oldMaxLength: 8000,
                oldDefaultValue: "[]"
            );

            migrationBuilder.AddColumn<string>(
                name: "config_json",
                table: "agentflow_edge",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "definition_fingerprint",
                table: "agentflow_checkpoint",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<Guid>(
                name: "summary_model_provider_id",
                table: "agentflow",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_agentflow_node",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "node_id" }
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_agentflow_edge",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "edge_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agentflow_id_kind_relate_id",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "kind", "relate_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_source_node_id",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "source_node_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_target_node_id",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "target_node_id" }
            );
        }
    }
}
