using Agw.Jobs.Domain.Enums;
using Agw.Shared.Contracts.Agents;

namespace Agw.Jobs.Contracts;

public class JobCreateRequest
{
    public Guid ProjectId { get; set; }
    public AgentRuntimeType? AgentType { get; set; }
    public Guid? AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Prompt { get; set; }
    public TriggerType TriggerType { get; set; }
    public string TriggerValue { get; set; } = string.Empty;
    public int MaxRetryCount { get; set; } = 3;
    public bool IsEnabled { get; set; } = true;
}

public class JobUpdateRequest
{
    public Guid ProjectId { get; set; }
    public AgentRuntimeType? AgentType { get; set; }
    public Guid? AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Prompt { get; set; }
    public TriggerType TriggerType { get; set; }
    public string TriggerValue { get; set; } = string.Empty;
    public int MaxRetryCount { get; set; } = 3;
    public bool IsEnabled { get; set; } = true;
    public JobStatus Status { get; set; } = JobStatus.Pending;
}
