using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
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

            migrationBuilder.RenameColumn(name: "execution_id", table: "execution_stream_entry", newName: "turn_id");

            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                table: "project_conversation_chat_history",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "history_scope",
                table: "project_conversation_chat_history",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "project_conversation_chat_history",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "message"
            );

            migrationBuilder.AddColumn<int>(
                name: "step_index",
                table: "project_conversation_chat_history",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "turn_id",
                table: "project_conversation_chat_history",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "seen_through_sequence",
                table: "project_conversation_binding",
                type: "bigint",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "lease_epoch",
                table: "execution_stream_entry",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<long>(
                name: "turn_sequence",
                table: "execution_stream_entry",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<long>(
                name: "last_event_sequence",
                table: "durable_execution",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<long>(
                name: "lease_epoch",
                table: "durable_execution",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "durable_execution",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "turn_checkpoint_json",
                table: "durable_execution",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "worker_id",
                table: "durable_execution",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "project_conversation_turn",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    runtime_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    input_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: true),
                    step_count = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_conversation_turn", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_h",
                table: "project_conversation_chat_history",
                columns: new[] { "project_conversation_id", "history_scope", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_p",
                table: "project_conversation_chat_history",
                columns: new[] { "project_conversation_id", "purpose", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_chat_history_turn_id_conversation_sequ",
                table: "project_conversation_chat_history",
                columns: new[] { "turn_id", "conversation_sequence" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_durable_execution_status_lease_expires_at",
                table: "durable_execution",
                columns: new[] { "status", "lease_expires_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_project_conversation_turn_project_conversation_id_first_seq",
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
                UPDATE execution_stream_entry AS entry
                SET turn_sequence = ranked.new_sequence
                FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY turn_id ORDER BY segment_index, "sequence", id) AS new_sequence
                    FROM execution_stream_entry
                ) AS ranked
                WHERE ranked.id = entry.id;
                """
            );

            migrationBuilder.DropColumn(name: "sequence", table: "execution_stream_entry");

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
                SET history_scope = metadata ->> 'historyScope',
                    metadata = metadata - 'historyScope'
                WHERE jsonb_typeof(metadata -> 'historyScope') = 'string';
                """
            );

            // 用户输入的锚点：role = user，没有节点作用域、agentflowInput 标记与 handoff 标记。
            // User input anchors: role = user without a node scope, the agentflowInput marker or the handoff marker.
            // 仅在识别消息用途时替换 JSON 中的 NUL 转义；原始 conversation_payload 保持不变。
            // Replace JSON NUL escapes only while classifying messages; preserve the original conversation_payload.
            migrationBuilder.Sql(
                """
                UPDATE project_conversation_chat_history
                SET purpose = 'input'
                WHERE conversation_sequence IS NOT NULL
                    AND history_scope IS NULL
                    AND (replace(conversation_payload, chr(92) || 'u0000', chr(92) || 'uFFFD')::jsonb ->> 'role') = 'user'
                    AND ((replace(conversation_payload, chr(92) || 'u0000', chr(92) || 'uFFFD')::jsonb -> 'additionalProperties' ->> 'agentflowInput')::boolean) IS NOT TRUE
                    AND ((replace(conversation_payload, chr(92) || 'u0000', chr(92) || 'uFFFD')::jsonb -> 'additionalProperties' ->> 'conversationHandoff')::boolean) IS NOT TRUE;
                """
            );
            migrationBuilder.Sql(
                """
                UPDATE project_conversation_chat_history
                SET purpose = 'result'
                WHERE purpose = 'message'
                    AND (
                        (metadata ->> 'purpose') = 'result'
                        OR (replace(conversation_payload, chr(92) || 'u0000', chr(92) || 'uFFFD')::jsonb -> 'additionalProperties' ->> 'type') = 'result'
                        OR (replace(conversation_payload, chr(92) || 'u0000', chr(92) || 'uFFFD')::jsonb -> 'additionalProperties' ->> 'messagePurpose') = 'result'
                        OR jsonb_path_exists(
                            replace(conversation_payload, chr(92) || 'u0000', chr(92) || 'uFFFD')::jsonb,
                            '$.contents[*].additionalProperties.type ? (@ == "result")'
                        )
                    );
                """
            );

            // 从一个锚点到下一个锚点之前的行归入同一个 Turn，Turn ID 取锚点行的 ID。
            // Rows from one anchor up to the next belong to one turn whose ID is the anchor row ID.
            migrationBuilder.Sql(
                """
                UPDATE project_conversation_chat_history AS history
                SET turn_id = (
                    SELECT anchor.id FROM project_conversation_chat_history AS anchor
                    WHERE anchor.project_conversation_id = history.project_conversation_id
                        AND anchor.purpose = 'input'
                        AND anchor.conversation_sequence <= history.conversation_sequence
                    ORDER BY anchor.conversation_sequence DESC
                    LIMIT 1
                )
                WHERE history.conversation_sequence IS NOT NULL;
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
                    COALESCE((anchor.metadata ->> 'targetId')::uuid, '00000000-0000-0000-0000-000000000000'::uuid),
                    CASE anchor.metadata ->> 'targetType' WHEN 'agentflow' THEN 'agentflow' ELSE 'agent' END,
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
                SET metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object('historyScope', history_scope)
                WHERE history_scope IS NOT NULL;
                """
            );

            migrationBuilder.DropTable(name: "project_conversation_turn");

            migrationBuilder.DropIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_h",
                table: "project_conversation_chat_history"
            );

            migrationBuilder.DropIndex(
                name: "ix_project_conversation_chat_history_project_conversation_id_p",
                table: "project_conversation_chat_history"
            );

            migrationBuilder.DropIndex(
                name: "ix_project_conversation_chat_history_turn_id_conversation_sequ",
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

            // Turn 内序号在同一 Turn 内唯一，写回 sequence 后满足旧的 (execution_id, segment_index, sequence) 唯一索引。
            // In-turn sequences are unique within a turn, so writing them back to sequence satisfies the old (execution_id, segment_index, sequence) unique index.
            migrationBuilder.AddColumn<int>(
                name: "sequence",
                table: "execution_stream_entry",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.Sql("UPDATE execution_stream_entry SET \"sequence\" = turn_sequence::integer;");

            migrationBuilder.DropColumn(name: "turn_sequence", table: "execution_stream_entry");

            migrationBuilder.DropColumn(name: "last_event_sequence", table: "durable_execution");

            migrationBuilder.DropColumn(name: "lease_epoch", table: "durable_execution");

            migrationBuilder.DropColumn(name: "lease_expires_at", table: "durable_execution");

            migrationBuilder.DropColumn(name: "turn_checkpoint_json", table: "durable_execution");

            migrationBuilder.DropColumn(name: "worker_id", table: "durable_execution");

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
