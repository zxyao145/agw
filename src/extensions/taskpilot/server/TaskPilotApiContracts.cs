using System.Text.Json;
using Agw.Agents.Contracts.Execution;

namespace Agw.Extensions.TaskPilot;

public sealed record TaskPilotDataScope
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EventPaths { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public int MaximumBytes { get; init; } = 16000;
}

public sealed record TaskPilotRetryPolicy
{
    public IReadOnlyList<int> DelayMinutes { get; init; } = [2, 4, 8, 16, 32];

    public int MaximumDelayMinutes { get; init; } = 60;

    public int NotificationThreshold { get; init; } = 3;

    public int StopThreshold { get; init; } = 5;
}

public sealed record TaskPilotRuleWriteRequest
{
    public Guid? Id { get; init; }

    public long? Version { get; init; }

    public required string Name { get; init; }

    public TaskPilotRuleKind Kind { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<Guid> SourceIds { get; init; } = [];

    public IReadOnlyList<string> EventTypes { get; init; } = [];

    public JsonElement? Condition { get; init; }

    public JsonElement? MatterKey { get; init; }

    public AgentTargetKind TargetKind { get; init; }

    public Guid? TargetId { get; init; }

    public Guid ProjectId { get; init; }

    public string Instructions { get; init; } = string.Empty;

    public string? Skill { get; init; }

    public TaskPilotDataScope DataScope { get; init; } = new();

    public AgentExecutionPermissionMode PermissionMode { get; init; }

    public IReadOnlyList<string> AskFirst { get; init; } = [];

    public string? GroupKey { get; init; }

    public int MaxConcurrency { get; init; } = 1;

    public TaskPilotSessionPolicy SessionPolicy { get; init; }

    public IReadOnlyList<string> Channels { get; init; } = [];

    public TaskPilotNotifyPolicy NotifyPolicy { get; init; }

    public IReadOnlyList<string> EmitTypes { get; init; } = [];

    public TaskPilotRetryPolicy Retry { get; init; } = new();

    public JsonElement? Delivery { get; init; }
}

public sealed record TaskPilotVersionRequest
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }
}

public sealed record TaskPilotMatterCancelRequest
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }

    public Guid? RoundId { get; init; }
}

public sealed record TaskPilotRuleSummary
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required TaskPilotRuleKind Kind { get; init; }

    public required bool Enabled { get; init; }

    public required bool Paused { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public required long Version { get; init; }

    public Guid? SubscriptionId { get; init; }

    public bool SubscriptionPending { get; init; }

    public string? SubscriptionError { get; init; }
}

public sealed record TaskPilotRuleDetails
{
    public required TaskPilotRuleSummary Rule { get; init; }

    public IReadOnlyList<Guid> SourceIds { get; init; } = [];

    public IReadOnlyList<string> EventTypes { get; init; } = [];

    public JsonElement? Condition { get; init; }

    public JsonElement? MatterKey { get; init; }

    public AgentTargetKind TargetKind { get; init; }

    public Guid? TargetId { get; init; }

    public Guid ProjectId { get; init; }

    public string Instructions { get; init; } = string.Empty;

    public string? Skill { get; init; }

    public TaskPilotDataScope DataScope { get; init; } = new();

    public AgentExecutionPermissionMode PermissionMode { get; init; }

    public IReadOnlyList<string> AskFirst { get; init; } = [];

    public string GroupKey { get; init; } = string.Empty;

    public int MaxConcurrency { get; init; }

    public TaskPilotSessionPolicy SessionPolicy { get; init; }

    public IReadOnlyList<string> Channels { get; init; } = [];

    public TaskPilotNotifyPolicy NotifyPolicy { get; init; }

    public IReadOnlyList<string> EmitTypes { get; init; } = [];

    public TaskPilotRetryPolicy Retry { get; init; } = new();

    public JsonElement? Delivery { get; init; }
}

public sealed record TaskPilotMatterSummary
{
    public required Guid Id { get; init; }

    public required Guid RuleId { get; init; }

    public required string MatterKey { get; init; }

    public required string Title { get; init; }

    public string? Link { get; init; }

    public required TaskPilotMatterState State { get; init; }

    public Guid? ConversationId { get; init; }

    public required int ConsecutiveFailures { get; init; }

    public required long Version { get; init; }

    public int QueuedRounds { get; init; }

    public Guid? ActiveRoundId { get; init; }
}

public sealed record TaskPilotMatterDetails
{
    public required TaskPilotMatterSummary Matter { get; init; }

    public IReadOnlyList<TaskPilotCardSummary> Cards { get; init; } = [];
}

public sealed record TaskPilotCardSummary
{
    public required Guid Id { get; init; }

    public required Guid RoundId { get; init; }

    public required TaskPilotCardKind Kind { get; init; }

    public required string CardKey { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public IReadOnlyList<Agw.Notifications.Contracts.NotificationOption> Options { get; init; } = [];

    public bool AllowText { get; init; }

    public int? Recommend { get; init; }

    public string? About { get; init; }

    public required TaskPilotCardState State { get; init; }

    public Guid? NotificationId { get; init; }

    public Guid? ConversationId { get; init; }

    public JsonElement? Answer { get; init; }

    public string? ResolvedReason { get; init; }
}

public sealed record TaskPilotRoundSummary
{
    public required Guid Id { get; init; }

    public required Guid MatterId { get; init; }

    public required TaskPilotRoundState State { get; init; }

    public required TaskPilotRoundTrigger Trigger { get; init; }

    public Guid? HeadEventId { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? TurnId { get; init; }

    public required int Attempt { get; init; }

    public TaskPilotRoundOutcome? Outcome { get; init; }

    public string? Summary { get; init; }

    public string? ReceiptError { get; init; }

    public string? Error { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public DateTimeOffset? NotBefore { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public required long Version { get; init; }
}

public sealed record TaskPilotRoundDetails
{
    public required TaskPilotRoundSummary Round { get; init; }

    public IReadOnlyList<TaskPilotRoundInputSummary> Inputs { get; init; } = [];

    public IReadOnlyList<TaskPilotRoundAttemptSummary> Attempts { get; init; } = [];

    public JsonElement? Receipt { get; init; }
}

public sealed record TaskPilotRoundInputSummary
{
    public required Guid EventId { get; init; }

    public required TaskPilotRoundInputKind Kind { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public Guid? ChainId { get; init; }

    public Guid? ParentEventId { get; init; }

    public required int Hops { get; init; }

    public string? Summary { get; init; }

    public string? Link { get; init; }

    public JsonElement? Event { get; init; }

    public JsonElement? Payload { get; init; }
}

public sealed record TaskPilotRoundAttemptSummary
{
    public required int Attempt { get; init; }

    public required Guid TurnId { get; init; }

    public required TaskPilotAttemptPurpose Purpose { get; init; }

    public required TaskPilotAttemptState State { get; init; }

    public string? Error { get; init; }

    public required long InputTokens { get; init; }

    public required long OutputTokens { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }
}

public sealed record TaskPilotReviewSummary
{
    public required Guid Id { get; init; }

    public required Guid MatterId { get; init; }

    public required string ReviewKey { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public string? About { get; init; }

    public required TaskPilotReviewState State { get; init; }

    public required long RequestSeq { get; init; }

    public required long Version { get; init; }
}

public sealed record TaskPilotDeliverySummary
{
    public required Guid Id { get; init; }

    public required Guid MatterId { get; init; }

    public required string Repository { get; init; }

    public required int PullRequestNumber { get; init; }

    public required TaskPilotDeliveryMode Mode { get; init; }

    public required string Stage { get; init; }

    public string? HeadSha { get; init; }

    public string? ReviewedSha { get; init; }

    public string? MergeSha { get; init; }

    public string? LastError { get; init; }

    public required long Version { get; init; }
}

public sealed record TaskPilotDeleteManagedSubscriptionRequest
{
    public required string ManagedKey { get; init; }

    public required long ManagedVersion { get; init; }
}
