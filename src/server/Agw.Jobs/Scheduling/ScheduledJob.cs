using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Scheduling;

/// <summary>
/// Immutable scheduling snapshot queued in memory. Its version lets the scheduler discard stale queue entries.
/// </summary>
public sealed record ScheduledJob
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

    public static ScheduledJob FromJob(Job job) =>
        new()
        {
            JobId = job.Id,
            ProjectId = job.ProjectId,
            AgentType = job.AgentType,
            AgentId = job.AgentId,
            Name = job.Name,
            Prompt = job.Prompt,
            TriggerType = job.TriggerType,
            TriggerValue = job.TriggerValue,
            NextRunTime = job.NextRunTime,
            RetryCount = job.RetryCount,
            MaxRetryCount = job.MaxRetryCount,
        };
}
