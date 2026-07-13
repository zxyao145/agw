using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Contracts.Agents;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agentflow_trace")]
public class AgentflowTrace
{
    public Guid Id { get; set; }

    public DateTimeOffset StartTimeUtc { get; set; }

    public Guid ProjectId { get; set; }

    public string ContextId { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public Guid AgentflowId { get; set; }

    public string NodeId { get; set; } = string.Empty;

    public string? NodeName { get; set; }

    public AgentflowNodeKind NodeKind { get; set; }

    public Guid? AgentId { get; set; }

    public string? AgentName { get; set; }

    public string Input { get; set; } = string.Empty;

    public long DurationMilliseconds { get; set; }

    public AgentflowNodeExecutionStatus Status { get; set; }

    public string? Error { get; set; }
}
