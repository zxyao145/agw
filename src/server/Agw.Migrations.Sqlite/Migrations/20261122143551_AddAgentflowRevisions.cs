using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
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

            // 显式创建使用新主键的空表，让迁移保持在同一事务内并遵守无外键规则。
            // Create the empty tables with their new keys explicitly to keep the migration transactional and foreign-key-free.
            migrationBuilder.DropTable("agentflow_edge");
            migrationBuilder.DropTable("agentflow_node");
            CreateRevisionNodes(migrationBuilder);
            CreateRevisionEdges(migrationBuilder);
            CreateBlockMembers(migrationBuilder);
            CreateRevisions(migrationBuilder);

            migrationBuilder.Sql("ALTER TABLE \"agentflow_checkpoint\" DROP COLUMN \"definition_fingerprint\";");
            migrationBuilder.Sql("ALTER TABLE \"agentflow\" DROP COLUMN \"summary_model_provider_id\";");
            AddRevisionColumns(migrationBuilder);
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
        }

        private static void CreateRevisionNodes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentflow_node",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    node_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    config_json = table.Column<string>(
                        type: "TEXT",
                        maxLength: 16000,
                        nullable: false,
                        defaultValue: "{}"
                    ),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    instructions = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    position_x = table.Column<double>(type: "REAL", nullable: false),
                    position_y = table.Column<double>(type: "REAL", nullable: false),
                    relate_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    routing_strategy = table.Column<int>(type: "INTEGER", nullable: false),
                    summary_model_provider_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                    table.PrimaryKey(
                        "pk_agentflow_node",
                        x => new
                        {
                            x.agentflow_id,
                            x.agentflow_revision,
                            x.node_id,
                        }
                    )
            );
            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agentflow_id_agentflow_revision_kind_relate_id",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "agentflow_revision", "kind", "relate_id" }
            );
        }

        private static void CreateRevisionEdges(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentflow_edge",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    edge_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    choice_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    condition_json = table.Column<string>(
                        type: "TEXT",
                        maxLength: 8000,
                        nullable: false,
                        defaultValue: "[]"
                    ),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    is_fan_in_barrier = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_switch_default = table.Column<bool>(type: "INTEGER", nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    source_node_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    switch_case_order = table.Column<int>(type: "INTEGER", nullable: false),
                    target_node_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                    table.PrimaryKey(
                        "pk_agentflow_edge",
                        x => new
                        {
                            x.agentflow_id,
                            x.agentflow_revision,
                            x.edge_id,
                        }
                    )
            );
            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_agentflow_revision_source_node_id",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "agentflow_revision", "source_node_id" }
            );
            migrationBuilder.CreateIndex(
                name: "ix_agentflow_edge_agentflow_id_agentflow_revision_target_node_id",
                table: "agentflow_edge",
                columns: new[] { "agentflow_id", "agentflow_revision", "target_node_id" }
            );
        }

        private static void CreateBlockMembers(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentflow_block_member",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agentflow_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    node_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    member_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    relate_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    instructions = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false),
                    is_manager = table.Column<bool>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                    table.PrimaryKey(
                        "pk_agentflow_block_member",
                        x => new
                        {
                            x.agentflow_id,
                            x.agentflow_revision,
                            x.node_id,
                            x.member_id,
                        }
                    )
            );
            migrationBuilder.CreateIndex(
                name: "ix_agentflow_block_member_agentflow_id_agentflow_revision_kind_relate_id",
                table: "agentflow_block_member",
                columns: new[] { "agentflow_id", "agentflow_revision", "kind", "relate_id" }
            );
        }

        private static void CreateRevisions(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentflow_revision",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_agentflow_revision", x => new { x.agentflow_id, x.revision })
            );
        }

        private static void AddRevisionColumns(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "agentflow_revision",
                table: "agentflow_checkpoint",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0
            );
            migrationBuilder.AddColumn<int>(
                name: "runtime_version",
                table: "agentflow_checkpoint",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0
            );
            migrationBuilder.AddColumn<int>(
                name: "latest_revision",
                table: "agentflow",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0
            );
            migrationBuilder.AddColumn<int>(
                name: "published_revision",
                table: "agentflow",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RestoreLegacyNodes(migrationBuilder);
            RestoreLegacyEdges(migrationBuilder);
            migrationBuilder.DropTable("agentflow_block_member");
            migrationBuilder.DropTable("agentflow_revision");
            migrationBuilder.Sql("ALTER TABLE \"project_conversation_turn\" DROP COLUMN \"extra\";");
            migrationBuilder.Sql("ALTER TABLE \"agentflow_trace\" DROP COLUMN \"extra\";");
            migrationBuilder.Sql("ALTER TABLE \"agentflow_checkpoint\" DROP COLUMN \"agentflow_revision\";");
            migrationBuilder.Sql("ALTER TABLE \"agentflow_checkpoint\" DROP COLUMN \"runtime_version\";");
            migrationBuilder.Sql("ALTER TABLE \"agentflow\" DROP COLUMN \"latest_revision\";");
            migrationBuilder.Sql("ALTER TABLE \"agentflow\" DROP COLUMN \"published_revision\";");
            migrationBuilder.AddColumn<string>(
                name: "definition_fingerprint",
                table: "agentflow_checkpoint",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: ""
            );
            migrationBuilder.AddColumn<Guid>(
                name: "summary_model_provider_id",
                table: "agentflow",
                type: "TEXT",
                nullable: true
            );
        }

        private static void RestoreLegacyNodes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable("agentflow_node", newName: "agentflow_node_with_revisions");
            migrationBuilder.CreateTable(
                name: "agentflow_node",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    config_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    instructions = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    position_json = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    relate_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_agentflow_node", x => new { x.agentflow_id, x.node_id })
            );
            migrationBuilder.Sql(
                """
                INSERT INTO agentflow_node (agentflow_id, node_id, config_json, create_by, create_time,
                    instructions, kind, name, relate_id, update_by, update_time)
                SELECT agentflow_id, node_id, config_json, create_by, create_time,
                    instructions, kind, name, relate_id, update_by, update_time
                FROM agentflow_node_with_revisions;
                """
            );
            migrationBuilder.DropTable("agentflow_node_with_revisions");
            migrationBuilder.CreateIndex(
                name: "ix_agentflow_node_agentflow_id_kind_relate_id",
                table: "agentflow_node",
                columns: new[] { "agentflow_id", "kind", "relate_id" }
            );
        }

        private static void RestoreLegacyEdges(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable("agentflow_edge", newName: "agentflow_edge_with_revisions");
            migrationBuilder.CreateTable(
                name: "agentflow_edge",
                columns: table => new
                {
                    agentflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    edge_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    condition_json = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    config_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    create_by = table.Column<string>(type: "TEXT", nullable: true),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    source_node_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    target_node_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_agentflow_edge", x => new { x.agentflow_id, x.edge_id })
            );
            migrationBuilder.Sql(
                """
                INSERT INTO agentflow_edge (agentflow_id, edge_id, condition_json, create_by, create_time,
                    kind, label, source_node_id, target_node_id, update_by, update_time)
                SELECT agentflow_id, edge_id, condition_json, create_by, create_time,
                    switch_case_order, label, source_node_id, target_node_id, update_by, update_time
                FROM agentflow_edge_with_revisions;
                """
            );
            migrationBuilder.DropTable("agentflow_edge_with_revisions");
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
