using Agw.Jobs.Domain.Enums;
using Agw.Shared.Enums;

namespace Agw.Jobs.Dtos;

public sealed class InMemoryJob
{
    public Guid JobId { get; init; }
    public Guid ProjectId { get; init; }
    public AgentRuntimeType? AgentType { get; init; }
    public Guid? AgentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Prompt { get; init; }

    public TriggerType TriggerType { get; init; }
    public string TriggerValue { get; init; } = string.Empty;
    public DateTimeOffset NextRunTime { get; init; }

    public int RetryCount { get; init; }
    public int MaxRetryCount { get; init; }

    public long Version { get; init; }
}
