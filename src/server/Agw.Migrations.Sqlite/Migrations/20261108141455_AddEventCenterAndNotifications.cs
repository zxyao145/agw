using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddEventCenterAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_execution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    caller_key = table.Column<string>(type: "TEXT", nullable: false),
                    correlation_key = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    target_kind = table.Column<int>(type: "INTEGER", nullable: true),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    tool_policy = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    result_text = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    input_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    output_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    request_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    chain_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_event_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    hops = table.Column<int>(type: "INTEGER", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_execution", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ce_spec_version = table.Column<string>(type: "TEXT", nullable: false),
                    ce_source = table.Column<string>(type: "TEXT", nullable: false),
                    ce_id = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    ce_time = table.Column<long>(type: "INTEGER", nullable: true),
                    subject = table.Column<string>(type: "TEXT", nullable: true),
                    ce_data_content_type = table.Column<string>(type: "TEXT", nullable: true),
                    ce_data_schema = table.Column<string>(type: "TEXT", nullable: true),
                    ce_data = table.Column<string>(type: "TEXT", nullable: true),
                    ce_data_is_binary = table.Column<bool>(type: "INTEGER", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: true),
                    tags_json = table.Column<string>(type: "TEXT", nullable: true),
                    summary = table.Column<string>(type: "TEXT", nullable: true),
                    link = table.Column<string>(type: "TEXT", nullable: true),
                    received_at = table.Column<long>(type: "INTEGER", nullable: false),
                    chain_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_event_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    hops = table.Column<int>(type: "INTEGER", nullable: false),
                    dispatch_state = table.Column<int>(type: "INTEGER", nullable: false),
                    matched_count = table.Column<int>(type: "INTEGER", nullable: false),
                    recover_after = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event_delivery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", nullable: true),
                    turn_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    handler_release_hash = table.Column<string>(type: "TEXT", nullable: true),
                    handler_epoch = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    recover_after = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                    request_snapshot_json = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_delivery", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event_delivery_attempt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    delivery_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    host_id = table.Column<string>(type: "TEXT", nullable: false),
                    generation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    ended_at = table.Column<long>(type: "INTEGER", nullable: false),
                    result = table.Column<int>(type: "INTEGER", nullable: false),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_delivery_attempt", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    topic = table.Column<string>(type: "TEXT", nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    transport_message_id = table.Column<string>(type: "TEXT", nullable: true),
                    published_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_outbox", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event_schedule",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    caller_key = table.Column<string>(type: "TEXT", nullable: false),
                    business_key = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: true),
                    due_at = table.Column<long>(type: "INTEGER", nullable: true),
                    interval = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    cron = table.Column<string>(type: "TEXT", nullable: true),
                    time_zone = table.Column<string>(type: "TEXT", nullable: true),
                    event_type = table.Column<string>(type: "TEXT", nullable: true),
                    subject = table.Column<string>(type: "TEXT", nullable: true),
                    data_content_type = table.Column<string>(type: "TEXT", nullable: true),
                    data_schema = table.Column<string>(type: "TEXT", nullable: true),
                    summary = table.Column<string>(type: "TEXT", nullable: true),
                    link = table.Column<string>(type: "TEXT", nullable: true),
                    data = table.Column<string>(type: "TEXT", nullable: true),
                    data_is_binary = table.Column<bool>(type: "INTEGER", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: true),
                    tags_json = table.Column<string>(type: "TEXT", nullable: true),
                    chain_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_event_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    hops = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    request_seq = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    occurrence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_fired_at = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_schedule", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event_source",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    provider_extension_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    publisher_key = table.Column<string>(type: "TEXT", nullable: true),
                    ce_source = table.Column<string>(type: "TEXT", nullable: false),
                    config_json = table.Column<string>(type: "TEXT", nullable: false),
                    secret_json = table.Column<string>(type: "TEXT", nullable: true),
                    cursor_json = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    baseline_at = table.Column<long>(type: "INTEGER", nullable: true),
                    next_run_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_run_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", nullable: true),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_source", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event_source_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_key = table.Column<string>(type: "TEXT", nullable: false),
                    fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    change_seq = table.Column<long>(type: "INTEGER", nullable: false),
                    first_seen_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_seen_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_event_id = table.Column<Guid>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_source_item", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event_subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    source_ids_json = table.Column<string>(type: "TEXT", nullable: false),
                    publisher_keys_json = table.Column<string>(type: "TEXT", nullable: true),
                    event_types_json = table.Column<string>(type: "TEXT", nullable: false),
                    filter_json = table.Column<string>(type: "TEXT", nullable: true),
                    target_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    instructions = table.Column<string>(type: "TEXT", nullable: true),
                    permission_mode = table.Column<int>(type: "INTEGER", nullable: true),
                    handler_extension_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    handler_id = table.Column<string>(type: "TEXT", nullable: true),
                    handler_config_json = table.Column<string>(type: "TEXT", nullable: true),
                    managed_by_extension_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    managed_key = table.Column<string>(type: "TEXT", nullable: true),
                    managed_version = table.Column<long>(type: "INTEGER", nullable: true),
                    managed_state = table.Column<int>(type: "INTEGER", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_subscription", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    link = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    origin_extension_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    caller_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    correlation_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    chain_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    parent_event_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    hops = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    read_at = table.Column<long>(type: "INTEGER", nullable: true),
                    changed_at = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    create_time = table.Column<long>(type: "INTEGER", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", nullable: false),
                    update_time = table.Column<long>(type: "INTEGER", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "notification_dispatch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    notification_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    channel_key = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    external_message_id = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    message_version = table.Column<long>(type: "INTEGER", nullable: false),
                    sent_version = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    claimed_by = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    claim_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    recover_after = table.Column<long>(type: "INTEGER", nullable: false),
                    claim_attempt_id = table.Column<Guid>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_dispatch", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "notification_dispatch_attempt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    dispatch_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    message_version = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at = table.Column<long>(type: "INTEGER", nullable: true),
                    result = table.Column<int>(type: "INTEGER", nullable: false),
                    external_message_id = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_dispatch_attempt", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "notification_question",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    options_json = table.Column<string>(type: "TEXT", nullable: false),
                    recommend = table.Column<int>(type: "INTEGER", nullable: true),
                    allow_text = table.Column<bool>(type: "INTEGER", nullable: false),
                    answer_option = table.Column<int>(type: "INTEGER", nullable: true),
                    answer_text = table.Column<string>(type: "TEXT", nullable: true),
                    answered_by = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    answered_via = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    answered_at = table.Column<long>(type: "INTEGER", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_question", x => x.notification_id);
                }
            );

            migrationBuilder.CreateTable(
                name: "notification_update_receipt",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    request_version = table.Column<long>(type: "INTEGER", nullable: false),
                    request_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_notification_update_receipt",
                        x => new { x.notification_id, x.request_version }
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_background_execution_create_by_caller_key_correlation_key",
                table: "background_execution",
                columns: new[] { "create_by", "caller_key", "correlation_key" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_background_execution_state",
                table: "background_execution",
                column: "state"
            );

            migrationBuilder.CreateIndex(name: "ix_event_chain_id", table: "event", column: "chain_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_create_by_received_at",
                table: "event",
                columns: new[] { "create_by", "received_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_create_by_type_received_at",
                table: "event",
                columns: new[] { "create_by", "type", "received_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_dispatch_state_recover_after",
                table: "event",
                columns: new[] { "dispatch_state", "recover_after" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_source_id_ce_source_ce_id",
                table: "event",
                columns: new[] { "source_id", "ce_source", "ce_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_delivery_event_id_subscription_id",
                table: "event_delivery",
                columns: new[] { "event_id", "subscription_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_delivery_state_next_attempt_at",
                table: "event_delivery",
                columns: new[] { "state", "next_attempt_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_delivery_state_recover_after",
                table: "event_delivery",
                columns: new[] { "state", "recover_after" }
            );

            migrationBuilder.CreateIndex(name: "ix_event_delivery_turn_id", table: "event_delivery", column: "turn_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_outbox_state_published_at",
                table: "event_outbox",
                columns: new[] { "state", "published_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_outbox_topic_entity_id",
                table: "event_outbox",
                columns: new[] { "topic", "entity_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_schedule_create_by_caller_key_business_key",
                table: "event_schedule",
                columns: new[] { "create_by", "caller_key", "business_key" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_schedule_state_due_at",
                table: "event_schedule",
                columns: new[] { "state", "due_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_source_create_by_publisher_key",
                table: "event_source",
                columns: new[] { "create_by", "publisher_key" },
                unique: true,
                filter: "publisher_key IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_source_enabled_next_run_at",
                table: "event_source",
                columns: new[] { "enabled", "next_run_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_source_item_source_id_item_key",
                table: "event_source_item",
                columns: new[] { "source_id", "item_key" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_subscription_create_by_enabled",
                table: "event_subscription",
                columns: new[] { "create_by", "enabled" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_event_subscription_create_by_managed_by_extension_id_managed_key",
                table: "event_subscription",
                columns: new[] { "create_by", "managed_by_extension_id", "managed_key" },
                unique: true,
                filter: "managed_key IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_create_by_caller_key_idempotency_key",
                table: "notification",
                columns: new[] { "create_by", "caller_key", "idempotency_key" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_create_by_changed_at_id",
                table: "notification",
                columns: new[] { "create_by", "changed_at", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_create_by_state",
                table: "notification",
                columns: new[] { "create_by", "state" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_origin_extension_id_correlation_key",
                table: "notification",
                columns: new[] { "origin_extension_id", "correlation_key" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_dispatch_notification_id_channel_key",
                table: "notification_dispatch",
                columns: new[] { "notification_id", "channel_key" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_dispatch_state_claim_expires_at",
                table: "notification_dispatch",
                columns: new[] { "state", "claim_expires_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_dispatch_state_next_attempt_at",
                table: "notification_dispatch",
                columns: new[] { "state", "next_attempt_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_dispatch_state_recover_after",
                table: "notification_dispatch",
                columns: new[] { "state", "recover_after" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_notification_dispatch_attempt_dispatch_id_started_at",
                table: "notification_dispatch_attempt",
                columns: new[] { "dispatch_id", "started_at" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "background_execution");

            migrationBuilder.DropTable(name: "event");

            migrationBuilder.DropTable(name: "event_delivery");

            migrationBuilder.DropTable(name: "event_delivery_attempt");

            migrationBuilder.DropTable(name: "event_outbox");

            migrationBuilder.DropTable(name: "event_schedule");

            migrationBuilder.DropTable(name: "event_source");

            migrationBuilder.DropTable(name: "event_source_item");

            migrationBuilder.DropTable(name: "event_subscription");

            migrationBuilder.DropTable(name: "notification");

            migrationBuilder.DropTable(name: "notification_dispatch");

            migrationBuilder.DropTable(name: "notification_dispatch_attempt");

            migrationBuilder.DropTable(name: "notification_question");

            migrationBuilder.DropTable(name: "notification_update_receipt");
        }
    }
}
