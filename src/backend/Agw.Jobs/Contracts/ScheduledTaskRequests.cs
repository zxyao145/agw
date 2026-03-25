using Agw.Jobs.Enums;
using Agw.Shared.Enums;

namespace Agw.Jobs.Contracts;

public class ScheduledTaskCreateRequest
{
    public Guid ProjectId { get; set; }
    public ProjectTaskAgentType? AgentType { get; set; }
    public Guid? AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Prompt { get; set; }
    public TriggerType TriggerType { get; set; }
    public string TriggerValue { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    public DateTimeOffset NextRunTime { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public bool IsEnabled { get; set; } = true;
}

public class ScheduledTaskUpdateRequest
{
    public Guid ProjectId { get; set; }
    public ProjectTaskAgentType? AgentType { get; set; }
    public Guid? AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Prompt { get; set; }
    public TriggerType TriggerType { get; set; }
    public string TriggerValue { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    public DateTimeOffset NextRunTime { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public bool IsEnabled { get; set; } = true;
    public ScheduledTaskStatus Status { get; set; } = ScheduledTaskStatus.Pending;
}
