using Agw.Agents.Contracts.Execution;
using Agw.Extensions.Sdk.Database;

namespace Agw.Extensions.TaskPilot;

public enum TaskPilotRuleKind
{
    Agent,
    Delivery,
}

public enum TaskPilotMatterState
{
    Active,
    WaitingHuman,
    Paused,
    Stopped,
}

public enum TaskPilotRoundState
{
    Queued,
    Submitting,
    Running,
    ReceiptRepair,
    Cancelling,
    Completed,
    Failed,
    Cancelled,
}

public enum TaskPilotRoundTrigger
{
    Event,
    Answer,
    Review,
    Retry,
    Manual,
}

public enum TaskPilotRoundInputKind
{
    Event,
    Answer,
    Review,
    Retry,
    Manual,
}

public enum TaskPilotAttemptPurpose
{
    Run,
    ReceiptRepair,
    DeliveryReview,
}

public enum TaskPilotAttemptState
{
    Submitting,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public enum TaskPilotCardKind
{
    Ask,
    Notice,
}

public enum TaskPilotCardState
{
    Open,
    Answered,
    Resolved,
    Closed,
}

public enum TaskPilotReviewState
{
    Scheduled,
    Due,
    Cancelled,
}

public enum TaskPilotNotifyPolicy
{
    Never,
    OnAttention,
    EveryRound,
}

public enum TaskPilotSessionPolicy
{
    PerMatter,
    PerRound,
}

public enum TaskPilotDeliveryMode
{
    Full,
    ReviewOnly,
}

public abstract class TaskPilotOwnedEntity : IExtensionOwnedEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string? CreateBy { get; set; }

    public DateTimeOffset CreateTime { get; set; }

    public string? UpdateBy { get; set; }

    public DateTimeOffset? UpdateTime { get; set; }
}

public sealed class TaskPilotUserState : TaskPilotOwnedEntity
{
    public long SystemSubscriptionVersion { get; set; }

    public Guid? SystemSubscriptionId { get; set; }

    public Guid? SystemSubscriptionOutboxId { get; set; }

    public string? SystemSubscriptionError { get; set; }
}

public sealed class TaskPilotRule : TaskPilotOwnedEntity
{
    public required string Name { get; set; }

    public TaskPilotRuleKind Kind { get; set; }

    public bool Enabled { get; set; }

    public bool Paused { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public long Version { get; set; }

    public Guid? SubscriptionId { get; set; }

    public Guid? SubscriptionOutboxId { get; set; }

    public string? SubscriptionError { get; set; }

    public string SourceIdsJson { get; set; } = "[]";

    public string ConditionJson { get; set; } = "{}";

    public string EventTypesJson { get; set; } = "[]";

    public string? MatterKeyJson { get; set; }

    public AgentTargetKind TargetKind { get; set; }

    public Guid? TargetId { get; set; }

    public Guid ProjectId { get; set; }

    public string Instructions { get; set; } = string.Empty;

    public string? Skill { get; set; }

    public string DataScopeJson { get; set; } = "{}";

    public AgentExecutionPermissionMode PermissionMode { get; set; }

    public string AskFirstJson { get; set; } = "[]";

    public string GroupKey { get; set; } = string.Empty;

    public int MaxConcurrency { get; set; }

    public TaskPilotSessionPolicy SessionPolicy { get; set; }

    public string ChannelsJson { get; set; } = "[]";

    public TaskPilotNotifyPolicy NotifyPolicy { get; set; }

    public string EmitTypesJson { get; set; } = "[]";

    public string RetryJson { get; set; } = "{}";

    public string DeliveryJson { get; set; } = "{}";
}

public sealed class TaskPilotRuleRevision : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotRule, Guid>
{
    public Guid RootId { get; set; }

    public long Version { get; set; }

    public string SnapshotJson { get; set; } = "{}";
}

public sealed class TaskPilotMatter : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotRule, Guid>
{
    public Guid RootId { get; set; }

    public required string MatterKey { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Link { get; set; }

    public TaskPilotMatterState State { get; set; }

    public Guid? ConversationId { get; set; }

    public int ConsecutiveFailures { get; set; }

    public long RetryRequestSeq { get; set; }

    public Guid? ChainId { get; set; }

    public Guid? FailureCardId { get; set; }

    public long Version { get; set; }
}

public sealed class TaskPilotRound : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotMatter, Guid>
{
    public Guid RootId { get; set; }

    public long RuleVersion { get; set; }

    public TaskPilotRoundState State { get; set; }

    public TaskPilotRoundTrigger Trigger { get; set; }

    public Guid? HeadEventId { get; set; }

    public Guid? ChainId { get; set; }

    public int HeadHops { get; set; }

    public Guid? ParentEventId { get; set; }

    public Guid? ConversationId { get; set; }

    public Guid? TurnId { get; set; }

    public int Attempt { get; set; }

    public TaskPilotRoundOutcome? Outcome { get; set; }

    public string? Summary { get; set; }

    public string? ReceiptJson { get; set; }

    public string? ReceiptError { get; set; }

    public string? Error { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public DateTimeOffset? NotBefore { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public long Version { get; set; }
}

public sealed class TaskPilotRoundInput : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotRound, Guid>
{
    public Guid RootId { get; set; }

    public Guid EventId { get; set; }

    public TaskPilotRoundInputKind Kind { get; set; }

    public string? EventJson { get; set; }

    public string? PayloadJson { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public Guid? ChainId { get; set; }

    public Guid? ParentEventId { get; set; }

    public int Hops { get; set; }

    public string? Summary { get; set; }

    public string? Link { get; set; }
}

public sealed class TaskPilotRoundAttempt : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotRound, Guid>
{
    public Guid RootId { get; set; }

    public int Attempt { get; set; }

    public Guid TurnId { get; set; }

    public TaskPilotAttemptPurpose Purpose { get; set; }

    public TaskPilotAttemptState State { get; set; }

    public Guid? SubmitOutboxId { get; set; }

    public Guid? CancelOutboxId { get; set; }

    public string? Error { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class TaskPilotCard : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotMatter, Guid>
{
    public Guid RootId { get; set; }

    public Guid RoundId { get; set; }

    public TaskPilotCardKind Kind { get; set; }

    public required string CardKey { get; set; }

    public string? OpenCardKey { get; set; }

    public required string Title { get; set; }

    public required string Text { get; set; }

    public string OptionsJson { get; set; } = "[]";

    public bool AllowText { get; set; }

    public int? Recommend { get; set; }

    public string? About { get; set; }

    public TaskPilotCardState State { get; set; }

    public Guid? NotificationId { get; set; }

    public Guid? NotificationOutboxId { get; set; }

    public Guid? NotificationUpdateOutboxId { get; set; }

    public long? NotificationUpdateExpectedVersion { get; set; }

    public long NotificationVersion { get; set; }

    public string? NotificationError { get; set; }

    public long Version { get; set; }

    public Guid? ConversationId { get; set; }

    public string? AnswerJson { get; set; }

    public string? ResolvedReason { get; set; }
}

public sealed class TaskPilotReview : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotMatter, Guid>
{
    public Guid RootId { get; set; }

    public required string ReviewKey { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public string? About { get; set; }

    public TaskPilotReviewState State { get; set; }

    public long RequestSeq { get; set; }

    public Guid? ScheduleId { get; set; }

    public Guid? ScheduleOutboxId { get; set; }

    public Guid? ChainId { get; set; }

    public Guid? ParentEventId { get; set; }

    public int Hops { get; set; }

    public long Version { get; set; }
}

public sealed class TaskPilotMatterActivity : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotMatter, Guid>
{
    public Guid RootId { get; set; }

    public Guid RoundId { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }
}

public sealed class TaskPilotConcurrencySlot : TaskPilotOwnedEntity
{
    public required string GroupKey { get; set; }

    public int SlotIndex { get; set; }

    public Guid RoundId { get; set; }
}

public sealed class TaskPilotDelivery : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotMatter, Guid>
{
    public Guid RootId { get; set; }

    public required string Repository { get; set; }

    public int PullRequestNumber { get; set; }

    public Guid? ReviewRoundId { get; set; }

    public TaskPilotDeliveryMode Mode { get; set; }

    public required string Stage { get; set; }

    public int SyncAttempts { get; set; }

    public string? HeadSha { get; set; }

    public string? BaseSha { get; set; }

    public string? ReviewedSha { get; set; }

    public string? MergeSha { get; set; }

    public long? CheckRunId { get; set; }

    public long? ShipRunId { get; set; }

    public string? LiveSha { get; set; }

    public string? FindingsJson { get; set; }

    public string? LastError { get; set; }

    public long Version { get; set; }
}

public sealed class TaskPilotDeliveryStage : TaskPilotOwnedEntity, IExtensionOwnedChild<TaskPilotDelivery, Guid>
{
    public Guid RootId { get; set; }

    public required string Stage { get; set; }

    public required string Result { get; set; }

    public string? DetailJson { get; set; }

    public DateTimeOffset At { get; set; }
}
