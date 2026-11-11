using Agw.Extensions.Sdk.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Agw.Extensions.TaskPilot;

public sealed class TaskPilotDbContext : ExtensionDbContext
{
    private static readonly ValueConverter<DateTimeOffset, long> UtcTicksConverter = new(
        value => value.UtcTicks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero)
    );

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableUtcTicksConverter = new(
        value => value.HasValue ? value.Value.UtcTicks : null,
        ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null
    );

    public TaskPilotDbContext(
        DbContextOptions<TaskPilotDbContext> options,
        IExtensionDatabaseIdentity identity,
        IExtensionUserContext userContext,
        TimeProvider timeProvider
    )
        : base(options, identity, userContext, timeProvider) { }

    public DbSet<TaskPilotUserState> UserStates => Set<TaskPilotUserState>();

    public DbSet<TaskPilotRule> Rules => Set<TaskPilotRule>();

    public DbSet<TaskPilotRuleRevision> RuleRevisions => Set<TaskPilotRuleRevision>();

    public DbSet<TaskPilotMatter> Matters => Set<TaskPilotMatter>();

    public DbSet<TaskPilotRound> Rounds => Set<TaskPilotRound>();

    public DbSet<TaskPilotRoundInput> RoundInputs => Set<TaskPilotRoundInput>();

    public DbSet<TaskPilotRoundAttempt> RoundAttempts => Set<TaskPilotRoundAttempt>();

    public DbSet<TaskPilotCard> Cards => Set<TaskPilotCard>();

    public DbSet<TaskPilotReview> Reviews => Set<TaskPilotReview>();

    public DbSet<TaskPilotMatterActivity> MatterActivities => Set<TaskPilotMatterActivity>();

    public DbSet<TaskPilotConcurrencySlot> ConcurrencySlots => Set<TaskPilotConcurrencySlot>();

    public DbSet<TaskPilotDelivery> Deliveries => Set<TaskPilotDelivery>();

    public DbSet<TaskPilotDeliveryStage> DeliveryStages => Set<TaskPilotDeliveryStage>();

    protected override void ConfigureExtensionModel(ModelBuilder modelBuilder)
    {
        string provider = Database.ProviderName!;
        string indexPrefix = $"zext_{ExtensionDatabaseIdentity.ExtensionId:N}_";

        modelBuilder.Entity<TaskPilotUserState>(entity =>
        {
            ConfigureOwned(entity, "user_state", provider);
            entity.Property(row => row.SystemSubscriptionVersion).HasColumnName("system_subscription_version");
            entity.Property(row => row.SystemSubscriptionId).HasColumnName("system_subscription_id");
            entity.Property(row => row.SystemSubscriptionOutboxId).HasColumnName("system_subscription_outbox_id");
            entity
                .Property(row => row.SystemSubscriptionError)
                .HasColumnName("system_subscription_error")
                .HasColumnType("text");
            entity.HasIndex(row => row.CreateBy).IsUnique().HasDatabaseName($"{indexPrefix}uq_user_state_owner");
        });

        modelBuilder.Entity<TaskPilotRule>(entity =>
        {
            ConfigureOwned(entity, "rule", provider);
            entity.Property(row => row.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(row => row.Kind).HasColumnName("kind").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.Enabled).HasColumnName("enabled");
            entity.Property(row => row.Paused).HasColumnName("paused");
            ConfigureNullableDateTime(entity.Property(row => row.DeletedAt).HasColumnName("deleted_at"), provider);
            entity.Property(row => row.Version).HasColumnName("version").IsConcurrencyToken();
            entity.Property(row => row.SubscriptionId).HasColumnName("subscription_id");
            entity.Property(row => row.SubscriptionOutboxId).HasColumnName("subscription_outbox_id");
            entity.Property(row => row.SubscriptionError).HasColumnName("subscription_error").HasColumnType("text");
            entity.Property(row => row.SourceIdsJson).HasColumnName("source_ids_json").HasColumnType("text");
            entity.Property(row => row.ConditionJson).HasColumnName("condition_json").HasColumnType("text");
            entity.Property(row => row.EventTypesJson).HasColumnName("event_types_json").HasColumnType("text");
            entity.Property(row => row.MatterKeyJson).HasColumnName("matter_key_json").HasColumnType("text");
            entity
                .Property(row => row.TargetKind)
                .HasColumnName("target_kind")
                .HasConversion<string>()
                .HasColumnType("text");
            entity.Property(row => row.TargetId).HasColumnName("target_id");
            entity.Property(row => row.ProjectId).HasColumnName("project_id");
            entity.Property(row => row.Instructions).HasColumnName("instructions").HasColumnType("text");
            entity.Property(row => row.Skill).HasColumnName("skill");
            entity.Property(row => row.DataScopeJson).HasColumnName("data_scope_json").HasColumnType("text");
            entity
                .Property(row => row.PermissionMode)
                .HasColumnName("permission_mode")
                .HasConversion<string>()
                .HasColumnType("text");
            entity.Property(row => row.AskFirstJson).HasColumnName("ask_first_json").HasColumnType("text");
            entity.Property(row => row.GroupKey).HasColumnName("group_key").HasMaxLength(200);
            entity.Property(row => row.MaxConcurrency).HasColumnName("max_concurrency");
            entity
                .Property(row => row.SessionPolicy)
                .HasColumnName("session_policy")
                .HasConversion<string>()
                .HasColumnType("text");
            entity.Property(row => row.ChannelsJson).HasColumnName("channels_json").HasColumnType("text");
            entity
                .Property(row => row.NotifyPolicy)
                .HasColumnName("notify_policy")
                .HasConversion<string>()
                .HasColumnType("text");
            entity.Property(row => row.EmitTypesJson).HasColumnName("emit_types_json").HasColumnType("text");
            entity.Property(row => row.RetryJson).HasColumnName("retry_json").HasColumnType("text");
            entity.Property(row => row.DeliveryJson).HasColumnName("delivery_json").HasColumnType("text");
        });

        modelBuilder.Entity<TaskPilotRuleRevision>(entity =>
        {
            ConfigureOwnedChild(entity, "rule_revision", provider);
            entity.Property(row => row.RootId).HasColumnName("rule_id");
            entity.Property(row => row.Version).HasColumnName("version");
            entity.Property(row => row.SnapshotJson).HasColumnName("snapshot_json").HasColumnType("text");
            entity
                .HasIndex(row => new { row.RootId, row.Version })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_revision_rule_version");
        });

        modelBuilder.Entity<TaskPilotMatter>(entity =>
        {
            ConfigureOwnedChild(entity, "matter", provider);
            entity.Property(row => row.RootId).HasColumnName("rule_id");
            entity.Property(row => row.MatterKey).HasColumnName("matter_key").HasMaxLength(500).IsRequired();
            entity.Property(row => row.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(row => row.Link).HasColumnName("link").HasColumnType("text");
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.ConversationId).HasColumnName("conversation_id");
            entity.Property(row => row.ConsecutiveFailures).HasColumnName("consecutive_failures");
            entity.Property(row => row.RetryRequestSeq).HasColumnName("retry_request_seq");
            entity.Property(row => row.ChainId).HasColumnName("chain_id");
            entity.Property(row => row.FailureCardId).HasColumnName("failure_card_id");
            entity.Property(row => row.Version).HasColumnName("version").IsConcurrencyToken();
            entity
                .HasIndex(row => new { row.RootId, row.MatterKey })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_matter_rule_key");
        });

        modelBuilder.Entity<TaskPilotRound>(entity =>
        {
            ConfigureOwnedChild(entity, "round", provider);
            entity.Property(row => row.RootId).HasColumnName("matter_id");
            entity.Property(row => row.RuleVersion).HasColumnName("rule_version");
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.Trigger).HasColumnName("trigger").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.HeadEventId).HasColumnName("head_event_id");
            entity.Property(row => row.ChainId).HasColumnName("chain_id");
            entity.Property(row => row.HeadHops).HasColumnName("head_hops");
            entity.Property(row => row.ParentEventId).HasColumnName("parent_event_id");
            entity.Property(row => row.ConversationId).HasColumnName("conversation_id");
            entity.Property(row => row.TurnId).HasColumnName("turn_id");
            entity.Property(row => row.Attempt).HasColumnName("attempt");
            entity.Property(row => row.Outcome).HasColumnName("outcome").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.Summary).HasColumnName("summary").HasColumnType("text");
            entity.Property(row => row.ReceiptJson).HasColumnName("receipt_json").HasColumnType("text");
            entity.Property(row => row.ReceiptError).HasColumnName("receipt_error").HasColumnType("text");
            entity.Property(row => row.Error).HasColumnName("error").HasColumnType("text");
            entity.Property(row => row.InputTokens).HasColumnName("input_tokens");
            entity.Property(row => row.OutputTokens).HasColumnName("output_tokens");
            ConfigureNullableDateTime(entity.Property(row => row.NotBefore).HasColumnName("not_before"), provider);
            ConfigureNullableDateTime(entity.Property(row => row.StartedAt).HasColumnName("started_at"), provider);
            ConfigureNullableDateTime(entity.Property(row => row.EndedAt).HasColumnName("ended_at"), provider);
            entity.Property(row => row.Version).HasColumnName("version").IsConcurrencyToken();
            entity
                .HasIndex(row => new { row.RootId, row.State })
                .HasDatabaseName($"{indexPrefix}ix_round_matter_state");
        });

        modelBuilder.Entity<TaskPilotRoundInput>(entity =>
        {
            ConfigureOwnedChild(entity, "round_input", provider);
            entity.Property(row => row.RootId).HasColumnName("round_id");
            entity.Property(row => row.EventId).HasColumnName("event_id");
            entity.Property(row => row.Kind).HasColumnName("kind").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.EventJson).HasColumnName("event_json").HasColumnType("text");
            entity.Property(row => row.PayloadJson).HasColumnName("payload_json").HasColumnType("text");
            ConfigureDateTime(entity.Property(row => row.ReceivedAt).HasColumnName("received_at"), provider);
            entity.Property(row => row.ChainId).HasColumnName("chain_id");
            entity.Property(row => row.ParentEventId).HasColumnName("parent_event_id");
            entity.Property(row => row.Hops).HasColumnName("hops");
            entity.Property(row => row.Summary).HasColumnName("summary").HasColumnType("text");
            entity.Property(row => row.Link).HasColumnName("link").HasColumnType("text");
            entity
                .HasIndex(row => new { row.RootId, row.EventId })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_input_round_event");
        });

        modelBuilder.Entity<TaskPilotRoundAttempt>(entity =>
        {
            ConfigureOwnedChild(entity, "round_attempt", provider);
            entity.Property(row => row.RootId).HasColumnName("round_id");
            entity.Property(row => row.Attempt).HasColumnName("attempt");
            entity.Property(row => row.TurnId).HasColumnName("turn_id");
            entity.Property(row => row.Purpose).HasColumnName("purpose").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.SubmitOutboxId).HasColumnName("submit_outbox_id");
            entity.Property(row => row.CancelOutboxId).HasColumnName("cancel_outbox_id");
            entity.Property(row => row.Error).HasColumnName("error").HasColumnType("text");
            entity.Property(row => row.InputTokens).HasColumnName("input_tokens");
            entity.Property(row => row.OutputTokens).HasColumnName("output_tokens");
            ConfigureNullableDateTime(entity.Property(row => row.StartedAt).HasColumnName("started_at"), provider);
            ConfigureNullableDateTime(entity.Property(row => row.EndedAt).HasColumnName("ended_at"), provider);
            entity
                .HasIndex(row => new { row.RootId, row.Attempt })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_attempt_round_number");
            entity.HasIndex(row => row.SubmitOutboxId).HasDatabaseName($"{indexPrefix}ix_attempt_submit_outbox");
            entity.HasIndex(row => row.CancelOutboxId).HasDatabaseName($"{indexPrefix}ix_attempt_cancel_outbox");
        });

        modelBuilder.Entity<TaskPilotCard>(entity =>
        {
            ConfigureOwnedChild(entity, "card", provider);
            entity.Property(row => row.RootId).HasColumnName("matter_id");
            entity.Property(row => row.RoundId).HasColumnName("round_id");
            entity.Property(row => row.Kind).HasColumnName("kind").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.CardKey).HasColumnName("card_key").HasMaxLength(200);
            entity.Property(row => row.OpenCardKey).HasColumnName("open_card_key").HasMaxLength(200);
            entity.Property(row => row.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(row => row.Text).HasColumnName("text").HasColumnType("text");
            entity.Property(row => row.OptionsJson).HasColumnName("options_json").HasColumnType("text");
            entity.Property(row => row.AllowText).HasColumnName("allow_text");
            entity.Property(row => row.Recommend).HasColumnName("recommend");
            entity.Property(row => row.About).HasColumnName("about").HasColumnType("text");
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.NotificationId).HasColumnName("notification_id");
            entity.Property(row => row.NotificationOutboxId).HasColumnName("notification_outbox_id");
            entity.Property(row => row.NotificationUpdateOutboxId).HasColumnName("notification_update_outbox_id");
            entity
                .Property(row => row.NotificationUpdateExpectedVersion)
                .HasColumnName("notification_update_expected_version");
            entity.Property(row => row.NotificationVersion).HasColumnName("notification_version");
            entity.Property(row => row.NotificationError).HasColumnName("notification_error").HasColumnType("text");
            entity.Property(row => row.Version).HasColumnName("version").IsConcurrencyToken();
            entity.Property(row => row.ConversationId).HasColumnName("conversation_id");
            entity.Property(row => row.AnswerJson).HasColumnName("answer_json").HasColumnType("text");
            entity.Property(row => row.ResolvedReason).HasColumnName("resolved_reason").HasColumnType("text");
            entity
                .HasIndex(row => new { row.RootId, row.OpenCardKey })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_card_matter_open_key");
            entity.HasIndex(row => row.NotificationOutboxId).HasDatabaseName($"{indexPrefix}ix_card_notify_outbox");
            entity
                .HasIndex(row => row.NotificationUpdateOutboxId)
                .HasDatabaseName($"{indexPrefix}ix_card_update_notify");
        });

        modelBuilder.Entity<TaskPilotReview>(entity =>
        {
            ConfigureOwnedChild(entity, "review", provider);
            entity.Property(row => row.RootId).HasColumnName("matter_id");
            entity.Property(row => row.ReviewKey).HasColumnName("review_key").HasMaxLength(200);
            ConfigureNullableDateTime(entity.Property(row => row.DueAt).HasColumnName("due_at"), provider);
            entity.Property(row => row.About).HasColumnName("about").HasColumnType("text");
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.RequestSeq).HasColumnName("request_seq");
            entity.Property(row => row.ScheduleId).HasColumnName("schedule_id");
            entity.Property(row => row.ScheduleOutboxId).HasColumnName("schedule_outbox_id");
            entity.Property(row => row.ChainId).HasColumnName("chain_id");
            entity.Property(row => row.ParentEventId).HasColumnName("parent_event_id");
            entity.Property(row => row.Hops).HasColumnName("hops");
            entity.Property(row => row.Version).HasColumnName("version").IsConcurrencyToken();
            entity
                .HasIndex(row => new { row.RootId, row.ReviewKey })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_review_matter_key");
            entity.HasIndex(row => row.ScheduleOutboxId).HasDatabaseName($"{indexPrefix}ix_review_schedule_outbox");
        });

        modelBuilder.Entity<TaskPilotMatterActivity>(entity =>
        {
            ConfigureOwnedChild(entity, "matter_activity", provider);
            entity.Property(row => row.RootId).HasColumnName("matter_id");
            entity.Property(row => row.RoundId).HasColumnName("round_id");
            ConfigureDateTime(entity.Property(row => row.AcquiredAt).HasColumnName("acquired_at"), provider);
            entity.HasIndex(row => row.RootId).IsUnique().HasDatabaseName($"{indexPrefix}uq_activity_matter");
            entity.HasIndex(row => row.RoundId).IsUnique().HasDatabaseName($"{indexPrefix}uq_activity_round");
        });

        modelBuilder.Entity<TaskPilotConcurrencySlot>(entity =>
        {
            ConfigureOwned(entity, "concurrency_slot", provider);
            entity.Property(row => row.GroupKey).HasColumnName("group_key").HasMaxLength(200).IsRequired();
            entity.Property(row => row.SlotIndex).HasColumnName("slot_index");
            entity.Property(row => row.RoundId).HasColumnName("round_id");
            entity
                .HasIndex(row => new
                {
                    row.CreateBy,
                    row.GroupKey,
                    row.SlotIndex,
                })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_slot_owner_group_index");
            entity.HasIndex(row => row.RoundId).IsUnique().HasDatabaseName($"{indexPrefix}uq_slot_round");
        });

        modelBuilder.Entity<TaskPilotDelivery>(entity =>
        {
            ConfigureOwnedChild(entity, "delivery", provider);
            entity.Property(row => row.RootId).HasColumnName("matter_id");
            entity.Property(row => row.Repository).HasColumnName("repository").HasMaxLength(300);
            entity.Property(row => row.PullRequestNumber).HasColumnName("pull_request_number");
            entity.Property(row => row.ReviewRoundId).HasColumnName("review_round_id");
            entity.Property(row => row.Mode).HasColumnName("mode").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.Stage).HasColumnName("stage").HasMaxLength(80);
            entity.Property(row => row.SyncAttempts).HasColumnName("sync_attempts");
            entity.Property(row => row.HeadSha).HasColumnName("head_sha").HasMaxLength(64);
            entity.Property(row => row.BaseSha).HasColumnName("base_sha").HasMaxLength(64);
            entity.Property(row => row.ReviewedSha).HasColumnName("reviewed_sha").HasMaxLength(64);
            entity.Property(row => row.MergeSha).HasColumnName("merge_sha").HasMaxLength(64);
            entity.Property(row => row.CheckRunId).HasColumnName("check_run_id");
            entity.Property(row => row.ShipRunId).HasColumnName("ship_run_id");
            entity.Property(row => row.LiveSha).HasColumnName("live_sha").HasMaxLength(64);
            entity.Property(row => row.FindingsJson).HasColumnName("findings_json").HasColumnType("text");
            entity.Property(row => row.LastError).HasColumnName("last_error").HasColumnType("text");
            entity.Property(row => row.Version).HasColumnName("version").IsConcurrencyToken();
            entity
                .HasIndex(row => new { row.RootId, row.PullRequestNumber })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_delivery_matter_pr");
        });

        modelBuilder.Entity<TaskPilotDeliveryStage>(entity =>
        {
            ConfigureOwnedChild(entity, "delivery_stage", provider);
            entity.Property(row => row.RootId).HasColumnName("delivery_id");
            entity.Property(row => row.Stage).HasColumnName("stage").HasMaxLength(80);
            entity.Property(row => row.Result).HasColumnName("result").HasMaxLength(40);
            entity.Property(row => row.DetailJson).HasColumnName("detail_json").HasColumnType("text");
            ConfigureDateTime(entity.Property(row => row.At).HasColumnName("at"), provider);
        });
    }

    private static void ConfigureOwned<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string provider)
        where TEntity : TaskPilotOwnedEntity
    {
        ConfigureBase(entity, table, provider);
    }

    private static void ConfigureOwnedChild<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string provider)
        where TEntity : TaskPilotOwnedEntity
    {
        ConfigureBase(entity, table, provider);
    }

    private static void ConfigureBase<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string provider)
        where TEntity : TaskPilotOwnedEntity
    {
        entity.ToTable(table);
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(row => row.CreateBy).HasColumnName("create_by").IsRequired();
        ConfigureDateTime(entity.Property(row => row.CreateTime).HasColumnName("create_time"), provider);
        entity.Property(row => row.UpdateBy).HasColumnName("update_by");
        ConfigureNullableDateTime(entity.Property(row => row.UpdateTime).HasColumnName("update_time"), provider);
    }

    private static void ConfigureDateTime(PropertyBuilder<DateTimeOffset> property, string provider)
    {
        if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            property.HasConversion(UtcTicksConverter).HasColumnType("INTEGER");
        }
        else
        {
            property.HasColumnType("timestamp with time zone");
        }
    }

    private static void ConfigureNullableDateTime(PropertyBuilder<DateTimeOffset?> property, string provider)
    {
        if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            property.HasConversion(NullableUtcTicksConverter).HasColumnType("INTEGER");
        }
        else
        {
            property.HasColumnType("timestamp with time zone");
        }
    }
}
