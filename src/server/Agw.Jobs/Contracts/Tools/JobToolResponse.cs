using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Contracts.Tools;

public sealed record JobToolResponse(
    Guid Id,
    Guid ProjectId,
    AgentRuntimeType? AgentType,
    Guid? AgentId,
    string Name,
    string? Prompt,
    TriggerType TriggerType,
    string TriggerValue,
    DateTimeOffset NextRunTime,
    JobStatus Status,
    bool IsEnabled,
    int RetryCount,
    int MaxRetryCount,
    string? LastError
);
