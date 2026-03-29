using Agw.Jobs.Enums;
using Agw.Shared;
using Agw.Shared.Enums;

namespace Agw.Domain.Entities;

public class Job : BaseEntity
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }
    public AgentRuntimeType? AgentType { get; set; }
    public Guid? AgentId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Prompt { get; set; }

    public TriggerType TriggerType { get; set; }
    public string TriggerValue { get; set; } = string.Empty;
    public DateTimeOffset NextRunTime { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public bool IsEnabled { get; set; } = true;

    public int RetryCount { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public string? LastError { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
