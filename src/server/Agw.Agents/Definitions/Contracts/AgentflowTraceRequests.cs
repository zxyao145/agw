namespace Agw.Agents.Definitions.Contracts;

public sealed record AgentflowTraceQuery
{
    public Guid? ProjectId { get; init; }

    public string? ContextId { get; init; }

    public Guid? AgentflowId { get; init; }

    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public int PageIndex { get; init; } = 1;

    public int PageSize { get; init; } = 20;
}
