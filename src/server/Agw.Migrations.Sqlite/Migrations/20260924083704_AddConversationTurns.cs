using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_execution_stream_entry_execution_id_segment_index_sequence",
                table: "execution_stream_entry"
            );

            migrationBuilder.RenameColumn(name: "sequence", table: "execution_stream_entry", newName: "turn_sequence");

            migrationBuilder.RenameColumn(name: "execution_id", table: "execution_stream_entry", newName: "turn_id");

            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                table: "project_conversation_chat_history",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "history_scope",
                table: "project_conversation_chat_history",
                type: "TEXT",
                maxLength: 256,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "project_conversation_chat_history",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "message"
            );

            migrationBuilder.AddColumn<int>(
                name: "step_index",
                table: "project_conversation_chat_history",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "turn_id",
                table: "project_conversation_chat_history",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "seen_through_sequence",
                table: "project_conversation_binding",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "lease_epoch",
                table: "execution_stream_entry",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<long>(
                name: "last_event_sequence",
                table: "durable_execution",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<long>(
                name: "lease_epoch",
                table: "durable_execution",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<long>(
                name: "lease_expires_at",
                table: "durable_execution",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "turn_checkpoint_json",
                table: "durable_execution",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "worker_id",
                table: "durable_execution",
                type: "TEXT",
                maxLength: 128,
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "project_conversation_turn",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    runtime_type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    input_message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    first_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: true),
                    step_count = table.Column<int>(type: "INTEGER", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_conversation_turn", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_history_scope_conversation_sequence",
                table: "project_conversation_chat_history",
                columns: new[] { "project_conversation_id", "history_scope", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_purpose_conversation_sequence",
                table: "project_conversation_chat_history",
                columns: new[] { "project_conversation_id", "purpose", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_turn_id_conversation_sequence",
                table: "project_conversation_chat_history",
                columns: new[] { "turn_id", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_status_lease_expires_at",
                table: "durable_execution",
                columns: new[] { "status", "lease_expires_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_turn_project_conversation_id_first_sequence",
                table: "project_conversation_turn",
                columns: new[] { "project_conversation_id", "first_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_turn_project_conversation_id_status",
                table: "project_conversation_turn",
                columns: new[] { "project_conversation_id", "status" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_turn_task_id",
                table: "project_conversation_turn",
                column: "task_id"
            );

            // 已有事件按原 segment 与 sequence 顺序改写为 Turn 内序号，并初始化 last_event_sequence。
            // Existing events are renumbered within their turn in the original segment and sequence order, and last_event_sequence is initialized.
            migrationBuilder.Sql(
                """
                UPDATE execution_stream_entry
                SET turn_sequence = ranked.new_sequence
                FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY turn_id ORDER BY segment_index, turn_sequence, id) AS new_sequence
                    FROM execution_stream_entry
                ) AS ranked
                WHERE ranked.id = execution_stream_entry.id;
                """
            );
            migrationBuilder.Sql(
                """
                UPDATE durable_execution
                SET last_event_sequence = COALESCE(
                    (SELECT MAX(entry.turn_sequence) FROM execution_stream_entry AS entry WHERE entry.turn_id = durable_execution.id),
                    0);
                """
            );

            // history_scope 从 metadata 提升为列。
            // history_scope is promoted from metadata to a column.
            migrationBuilder.Sql(
                """
                UPDATE project_conversation_chat_history
                SET history_scope = json_extract(metadata, '$.historyScope'),
                    metadata = json_remove(metadata, '$.historyScope')
                WHERE json_type(metadata, '$.historyScope') = 'text';
                """
            );

            // 用户输入的锚点：role = user，没有节点作用域、agentflowInput 标记与 handoff 标记。
            // User input anchors: role = user without a node scope, the agentflowInput marker or the handoff marker.
            migrationBuilder.Sql(
                """
                UPDATE project_conversation_chat_history
                SET purpose = 'input'
                WHERE conversation_sequence IS NOT NULL
                    AND history_scope IS NULL
                    AND json_extract(conversation_payload, '$.role') = 'user'
                    AND COALESCE(json_extract(conversation_payload, '$.additionalProperties.agentflowInput'), 0) = 0
                    AND COALESCE(json_extract(conversation_payload, '$.additionalProperties.conversationHandoff'), 0) = 0;
                """
            );
            migrationBuilder.Sql(
                """
                UPDATE project_conversation_chat_history
                SET purpose = 'result'
                WHERE purpose = 'message'
                    AND (
                        json_extract(metadata, '$.purpose') = 'result'
                        OR json_extract(conversation_payload, '$.additionalProperties.type') = 'result'
                        OR json_extract(conversation_payload, '$.additionalProperties.messagePurpose') = 'result'
                        OR EXISTS (
                            SELECT 1 FROM json_each(conversation_payload, '$.contents') AS content
                            WHERE json_extract(content.value, '$.additionalProperties.type') = 'result'
                        )
                    );
                """
            );

            // 从一个锚点到下一个锚点之前的行归入同一个 Turn，Turn ID 取锚点行的 ID。
            // Rows from one anchor up to the next belong to one turn whose ID is the anchor row ID.
            migrationBuilder.Sql(
                """
                UPDATE project_conversation_chat_history
                SET turn_id = (
                    SELECT anchor.id FROM project_conversation_chat_history AS anchor
                    WHERE anchor.project_conversation_id = project_conversation_chat_history.project_conversation_id
                        AND anchor.purpose = 'input'
                        AND anchor.conversation_sequence <= project_conversation_chat_history.conversation_sequence
                    ORDER BY anchor.conversation_sequence DESC
                    LIMIT 1
                )
                WHERE conversation_sequence IS NOT NULL;
                """
            );
            migrationBuilder.Sql(
                """
                INSERT INTO project_conversation_turn
                    (id, project_conversation_id, task_id, target_id, runtime_type, status, input_message_id,
                     first_sequence, last_sequence, step_count, started_at, finished_at, error_code)
                SELECT
                    anchor.id,
                    anchor.project_conversation_id,
                    anchor.task_id,
                    COALESCE(upper(json_extract(anchor.metadata, '$.targetId')), '00000000-0000-0000-0000-000000000000'),
                    CASE json_extract(anchor.metadata, '$.targetType') WHEN 'agentflow' THEN 'agentflow' ELSE 'agent' END,
                    'completed',
                    anchor.id,
                    anchor.conversation_sequence,
                    (SELECT MAX(member.conversation_sequence) FROM project_conversation_chat_history AS member
                     WHERE member.turn_id = anchor.id),
                    0,
                    anchor.create_time,
                    (SELECT COALESCE(member.update_time, member.create_time) FROM project_conversation_chat_history AS member
                     WHERE member.turn_id = anchor.id ORDER BY member.conversation_sequence DESC LIMIT 1),
                    NULL
                FROM project_conversation_chat_history AS anchor
                WHERE anchor.purpose = 'input';
                """
            );

            migrationBuilder.CreateIndex(
                name: "ix_execution_stream_entry_turn_id_turn_sequence",
                table: "execution_stream_entry",
                columns: new[] { "turn_id", "turn_sequence" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 删除列之前把 history_scope 写回 metadata。
            // history_scope is written back to metadata before the column is dropped.
            migrationBuilder.Sql(
                """
                UPDATE project_conversation_chat_history
                SET metadata = json_set(COALESCE(metadata, '{}'), '$.historyScope', history_scope)
                WHERE history_scope IS NOT NULL;
                """
            );

            migrationBuilder.DropTable(name: "project_conversation_turn");

            migrationBuilder.DropIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_history_scope_conversation_sequence",
                table: "project_conversation_chat_history"
            );

            migrationBuilder.DropIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_purpose_conversation_sequence",
                table: "project_conversation_chat_history"
            );

            migrationBuilder.DropIndex(
                name: "ix_project_conversation_chat_history_turn_id_conversation_sequence",
                table: "project_conversation_chat_history"
            );

            migrationBuilder.DropIndex(
                name: "ix_execution_stream_entry_turn_id_turn_sequence",
                table: "execution_stream_entry"
            );

            migrationBuilder.DropIndex(
                name: "ix_durable_execution_status_lease_expires_at",
                table: "durable_execution"
            );

            migrationBuilder.DropColumn(name: "agent_id", table: "project_conversation_chat_history");

            migrationBuilder.DropColumn(name: "history_scope", table: "project_conversation_chat_history");

            migrationBuilder.DropColumn(name: "purpose", table: "project_conversation_chat_history");

            migrationBuilder.DropColumn(name: "step_index", table: "project_conversation_chat_history");

            migrationBuilder.DropColumn(name: "turn_id", table: "project_conversation_chat_history");

            migrationBuilder.DropColumn(name: "seen_through_sequence", table: "project_conversation_binding");

            migrationBuilder.DropColumn(name: "lease_epoch", table: "execution_stream_entry");

            migrationBuilder.DropColumn(name: "last_event_sequence", table: "durable_execution");

            migrationBuilder.DropColumn(name: "lease_epoch", table: "durable_execution");

            migrationBuilder.DropColumn(name: "lease_expires_at", table: "durable_execution");

            migrationBuilder.DropColumn(name: "turn_checkpoint_json", table: "durable_execution");

            migrationBuilder.DropColumn(name: "worker_id", table: "durable_execution");

            migrationBuilder.RenameColumn(name: "turn_sequence", table: "execution_stream_entry", newName: "sequence");

            migrationBuilder.RenameColumn(name: "turn_id", table: "execution_stream_entry", newName: "execution_id");

            migrationBuilder.CreateIndex(
                name: "ix_execution_stream_entry_execution_id_segment_index_sequence",
                table: "execution_stream_entry",
                columns: new[] { "execution_id", "segment_index", "sequence" },
                unique: true
            );
        }
    }
}
