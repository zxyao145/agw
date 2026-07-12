namespace Agw.Shared.Contracts.Agents;

public sealed class AgentflowTraceDto
{
    public Guid Id { get; init; }

    public DateTime StartTimeUtc { get; init; }

    public Guid ProjectId { get; init; }

    public string ContextId { get; init; } = string.Empty;

    public Guid TaskId { get; init; }

    public Guid AgentflowId { get; init; }

    public string NodeId { get; init; } = string.Empty;

    public string? NodeName { get; init; }

    public AgentflowNodeKind NodeKind { get; init; }

    public Guid? AgentId { get; init; }

    public string? AgentName { get; init; }

    public string Input { get; init; } = string.Empty;

    public long DurationMilliseconds { get; init; }

    public AgentflowNodeExecutionStatus Status { get; init; }

    public string? Error { get; init; }
}
