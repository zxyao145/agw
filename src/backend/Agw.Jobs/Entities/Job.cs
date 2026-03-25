using Agw.Jobs.Enums;
using Agw.Shared;
using Agw.Shared.Enums;
using System.ComponentModel.DataAnnotations;

namespace Agw.Domain.Entities;

public class Job : BaseEntity
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }
    public ProjectTaskAgentType? AgentType { get; set; }
    public Guid? AgentId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Prompt { get; set; }

    public TriggerType TriggerType { get; set; }
    public string TriggerValue { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "Asia/Shanghai";

    public DateTimeOffset NextRunTime { get; set; }

    public ScheduledTaskStatus Status { get; set; } = ScheduledTaskStatus.Pending;
    public bool IsEnabled { get; set; } = true;

    public int RetryCount { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public string? LastError { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
