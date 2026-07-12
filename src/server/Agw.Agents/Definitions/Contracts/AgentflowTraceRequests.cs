namespace Agw.Agents.Definitions.Contracts;

public sealed record AgentflowTraceQuery
{
    public Guid? ProjectId { get; init; }

    public string? ContextId { get; init; }

    public Guid? AgentflowId { get; init; }

    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }

    public int PageIndex { get; init; } = 1;

    public int PageSize { get; init; } = 20;
}
