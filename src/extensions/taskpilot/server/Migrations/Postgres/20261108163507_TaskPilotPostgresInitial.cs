using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Extensions.TaskPilot.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class TaskPilotPostgresInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101___inbox",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_json = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101___inbox", x => x.delivery_id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101___outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    result_json = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<int>(type: "integer", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101___outbox", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_card",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    matter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    card_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    open_card_key = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    options_json = table.Column<string>(type: "text", nullable: false),
                    allow_text = table.Column<bool>(type: "boolean", nullable: false),
                    recommend = table.Column<int>(type: "integer", nullable: true),
                    about = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notification_outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notification_update_outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notification_update_expected_version = table.Column<long>(type: "bigint", nullable: true),
                    notification_version = table.Column<long>(type: "bigint", nullable: false),
                    notification_error = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    answer_json = table.Column<string>(type: "text", nullable: true),
                    resolved_reason = table.Column<string>(type: "text", nullable: true),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_card", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_concurrency_slot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slot_index = table.Column<int>(type: "integer", nullable: false),
                    round_id = table.Column<Guid>(type: "uuid", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_concurrency_slot", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_delivery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    matter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    review_round_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    sync_attempts = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    base_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reviewed_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    merge_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    check_run_id = table.Column<long>(type: "bigint", nullable: true),
                    ship_run_id = table.Column<long>(type: "bigint", nullable: true),
                    live_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    findings_json = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_delivery", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_delivery_stage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    result = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    detail_json = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_delivery_stage", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_matter",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    matter_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    link = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    retry_request_seq = table.Column<long>(type: "bigint", nullable: false),
                    chain_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_matter", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_matter_activity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    matter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_matter_activity", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    matter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    about = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    request_seq = table.Column<long>(type: "bigint", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    schedule_outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chain_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hops = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_review", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_round",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    matter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    head_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chain_id = table.Column<Guid>(type: "uuid", nullable: true),
                    head_hops = table.Column<int>(type: "integer", nullable: false),
                    parent_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    turn_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    receipt_json = table.Column<string>(type: "text", nullable: true),
                    receipt_error = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_round", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_round_attempt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    turn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    submit_outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_round_attempt", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_round_input",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    event_json = table.Column<string>(type: "text", nullable: true),
                    payload_json = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    chain_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hops = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    link = table.Column<string>(type: "text", nullable: true),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_round_input", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_rule",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_error = table.Column<string>(type: "text", nullable: true),
                    source_ids_json = table.Column<string>(type: "text", nullable: false),
                    condition_json = table.Column<string>(type: "text", nullable: false),
                    event_types_json = table.Column<string>(type: "text", nullable: false),
                    matter_key_json = table.Column<string>(type: "text", nullable: true),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: false),
                    skill = table.Column<string>(type: "text", nullable: true),
                    data_scope_json = table.Column<string>(type: "text", nullable: false),
                    permission_mode = table.Column<string>(type: "text", nullable: false),
                    ask_first_json = table.Column<string>(type: "text", nullable: false),
                    group_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    max_concurrency = table.Column<int>(type: "integer", nullable: false),
                    session_policy = table.Column<string>(type: "text", nullable: false),
                    channels_json = table.Column<string>(type: "text", nullable: false),
                    notify_policy = table.Column<string>(type: "text", nullable: false),
                    emit_types_json = table.Column<string>(type: "text", nullable: false),
                    retry_json = table.Column<string>(type: "text", nullable: false),
                    delivery_json = table.Column<string>(type: "text", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_rule", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_rule_revision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_json = table.Column<string>(type: "text", nullable: false),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_rule_revision", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "zext_019a4c175f007a808b3d2b732f48a101_user_state",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    system_subscription_version = table.Column<long>(type: "bigint", nullable: false),
                    system_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    system_subscription_outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    system_subscription_error = table.Column<string>(type: "text", nullable: true),
                    create_by = table.Column<string>(type: "text", nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    update_by = table.Column<string>(type: "text", nullable: true),
                    update_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zext_019a4c175f007a808b3d2b732f48a101_user_state", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_outbox_key",
                table: "zext_019a4c175f007a808b3d2b732f48a101___outbox",
                column: "idempotency_key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_ix_card_notify_outbox",
                table: "zext_019a4c175f007a808b3d2b732f48a101_card",
                column: "notification_outbox_id"
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_ix_card_update_notify",
                table: "zext_019a4c175f007a808b3d2b732f48a101_card",
                column: "notification_update_outbox_id"
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_card_matter_open_key",
                table: "zext_019a4c175f007a808b3d2b732f48a101_card",
                columns: new[] { "matter_id", "open_card_key" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_slot_owner_group_index",
                table: "zext_019a4c175f007a808b3d2b732f48a101_concurrency_slot",
                columns: new[] { "create_by", "group_key", "slot_index" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_slot_round",
                table: "zext_019a4c175f007a808b3d2b732f48a101_concurrency_slot",
                column: "round_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_delivery_matter_pr",
                table: "zext_019a4c175f007a808b3d2b732f48a101_delivery",
                columns: new[] { "matter_id", "pull_request_number" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_matter_rule_key",
                table: "zext_019a4c175f007a808b3d2b732f48a101_matter",
                columns: new[] { "rule_id", "matter_key" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_activity_matter",
                table: "zext_019a4c175f007a808b3d2b732f48a101_matter_activity",
                column: "matter_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_activity_round",
                table: "zext_019a4c175f007a808b3d2b732f48a101_matter_activity",
                column: "round_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_ix_review_schedule_outbox",
                table: "zext_019a4c175f007a808b3d2b732f48a101_review",
                column: "schedule_outbox_id"
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_review_matter_key",
                table: "zext_019a4c175f007a808b3d2b732f48a101_review",
                columns: new[] { "matter_id", "review_key" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_ix_round_matter_state",
                table: "zext_019a4c175f007a808b3d2b732f48a101_round",
                columns: new[] { "matter_id", "state" }
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_ix_attempt_cancel_outbox",
                table: "zext_019a4c175f007a808b3d2b732f48a101_round_attempt",
                column: "cancel_outbox_id"
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_ix_attempt_submit_outbox",
                table: "zext_019a4c175f007a808b3d2b732f48a101_round_attempt",
                column: "submit_outbox_id"
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_attempt_round_number",
                table: "zext_019a4c175f007a808b3d2b732f48a101_round_attempt",
                columns: new[] { "round_id", "attempt" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_input_round_event",
                table: "zext_019a4c175f007a808b3d2b732f48a101_round_input",
                columns: new[] { "round_id", "event_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_revision_rule_version",
                table: "zext_019a4c175f007a808b3d2b732f48a101_rule_revision",
                columns: new[] { "rule_id", "version" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "zext_019a4c175f007a808b3d2b732f48a101_uq_user_state_owner",
                table: "zext_019a4c175f007a808b3d2b732f48a101_user_state",
                column: "create_by",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101___inbox");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101___outbox");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_card");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_concurrency_slot");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_delivery");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_delivery_stage");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_matter");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_matter_activity");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_review");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_round");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_round_attempt");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_round_input");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_rule");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_rule_revision");

            migrationBuilder.DropTable(name: "zext_019a4c175f007a808b3d2b732f48a101_user_state");
        }
    }
}
