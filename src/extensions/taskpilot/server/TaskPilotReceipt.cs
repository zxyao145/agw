using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Contracts.Messages;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Notifications.Contracts;
using Agw.Shared.Contracts.Events;

namespace Agw.Extensions.TaskPilot;

public enum TaskPilotRoundOutcome
{
    Completed,
    NoWork,
    RetryableFailure,
}

public sealed record TaskPilotReceipt
{
    public IReadOnlyList<TaskPilotReceiptAsk> Ask { get; init; } = [];

    public IReadOnlyList<TaskPilotReceiptNotice> Notice { get; init; } = [];

    public IReadOnlyList<TaskPilotReceiptResolve> Resolve { get; init; } = [];

    public IReadOnlyList<TaskPilotReceiptLater> Later { get; init; } = [];

    public IReadOnlyList<TaskPilotReceiptEvent> Events { get; init; } = [];
}

public sealed record TaskPilotReceiptAsk
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public IReadOnlyList<Agw.Notifications.Contracts.NotificationOption> Options { get; init; } = [];

    public int? Recommend { get; init; }

    public string? About { get; init; }
}

public sealed record TaskPilotReceiptNotice
{
    public required string Title { get; init; }

    public required string Text { get; init; }

    public bool Fyi { get; init; }
}

public sealed record TaskPilotReceiptResolve
{
    public required string Card { get; init; }

    public required string Reason { get; init; }
}

public sealed record TaskPilotReceiptLater
{
    public required string Key { get; init; }

    public DateTimeOffset? At { get; init; }

    public bool Cancel { get; init; }

    public string? About { get; init; }
}

public sealed record TaskPilotReceiptEvent
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public string? Subject { get; init; }

    public string? Scope { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Summary { get; init; }

    public string? Link { get; init; }

    public JsonElement? Detail { get; init; }
}

public sealed record TaskPilotReceiptParseResult
{
    public required bool IsValid { get; init; }

    public TaskPilotReceipt? Receipt { get; init; }

    public string? Error { get; init; }

    public required TaskPilotRoundOutcome Outcome { get; init; }

    public required string Summary { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TaskPilotReceipt))]
[JsonSerializable(typeof(TaskPilotReviewDueData))]
[JsonSerializable(typeof(TaskPilotRetryDueData))]
[JsonSerializable(typeof(TaskPilotCancelReviewScheduleRequest))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(TaskPilotRuleWriteRequest))]
[JsonSerializable(typeof(TaskPilotVersionRequest))]
[JsonSerializable(typeof(TaskPilotDeleteManagedSubscriptionRequest))]
[JsonSerializable(typeof(TaskPilotMatterCancelRequest))]
[JsonSerializable(typeof(TaskPilotRuleSummary))]
[JsonSerializable(typeof(TaskPilotRuleSummary[]))]
[JsonSerializable(typeof(TaskPilotRuleDetails))]
[JsonSerializable(typeof(TaskPilotMatterSummary))]
[JsonSerializable(typeof(TaskPilotMatterSummary[]))]
[JsonSerializable(typeof(TaskPilotMatterDetails))]
[JsonSerializable(typeof(TaskPilotCardSummary))]
[JsonSerializable(typeof(TaskPilotCardSummary[]))]
[JsonSerializable(typeof(TaskPilotRoundSummary))]
[JsonSerializable(typeof(TaskPilotRoundSummary[]))]
[JsonSerializable(typeof(TaskPilotRoundDetails))]
[JsonSerializable(typeof(TaskPilotRoundInputSummary))]
[JsonSerializable(typeof(TaskPilotRoundInputSummary[]))]
[JsonSerializable(typeof(TaskPilotRoundAttemptSummary))]
[JsonSerializable(typeof(TaskPilotRoundAttemptSummary[]))]
[JsonSerializable(typeof(TaskPilotReviewSummary))]
[JsonSerializable(typeof(TaskPilotReviewSummary[]))]
[JsonSerializable(typeof(TaskPilotDeliverySummary))]
[JsonSerializable(typeof(TaskPilotDeliverySummary[]))]
[JsonSerializable(typeof(TaskPilotDataScope))]
[JsonSerializable(typeof(TaskPilotRetryPolicy))]
[JsonSerializable(typeof(ManagedSubscriptionRequest))]
[JsonSerializable(typeof(ManagedSubscriptionState))]
[JsonSerializable(typeof(ScheduleRequest))]
[JsonSerializable(typeof(ScheduleState))]
[JsonSerializable(typeof(BackgroundExecutionRequest))]
[JsonSerializable(typeof(BackgroundExecutionReceipt))]
[JsonSerializable(typeof(BackgroundExecutionCancelRequest))]
[JsonSerializable(typeof(BackgroundExecutionStatus))]
[JsonSerializable(typeof(AgentTarget))]
[JsonSerializable(typeof(AgwUserInput))]
[JsonSerializable(typeof(NotificationRequest))]
[JsonSerializable(typeof(NotificationUpdate))]
[JsonSerializable(
    typeof(Agw.Notifications.Contracts.NotificationOption),
    TypeInfoPropertyName = "NotificationChannelOption"
)]
[JsonSerializable(typeof(CloudEventDraft))]
[JsonSerializable(typeof(CloudEventEnvelope))]
[JsonSerializable(typeof(CloudEventData))]
[JsonSerializable(typeof(ExtensionEventDelivery))]
[JsonSerializable(typeof(ExtensionEventHandlerResult))]
[JsonSerializable(typeof(ExtensionOutboxResult))]
[JsonSerializable(typeof(TaskPilotRuleHandlerConfig))]
public partial class TaskPilotJsonContext : JsonSerializerContext { }
