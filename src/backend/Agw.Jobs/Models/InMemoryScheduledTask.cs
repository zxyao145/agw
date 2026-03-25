using Agw.Jobs.Enums;
using Agw.Shared.Enums;

namespace Agw.Jobs.Models;

public sealed class InMemoryJob
{
    public Guid JobId { get; init; }
    public Guid ProjectId { get; init; }
    public ProjectTaskAgentType? AgentType { get; init; }
    public Guid? AgentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Prompt { get; init; }

    public TriggerType TriggerType { get; init; }
    public string TriggerValue { get; init; } = string.Empty;
    public string TimeZoneId { get; init; } = "UTC";

    public DateTimeOffset NextRunTime { get; init; }

    public int RetryCount { get; init; }
    public int MaxRetryCount { get; init; }

    public long Version { get; init; }
}
