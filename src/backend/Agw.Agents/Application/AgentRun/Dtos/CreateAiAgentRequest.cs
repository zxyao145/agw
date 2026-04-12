namespace Agw.Agents.Application.AgentRun.Dtos;

public sealed class CreateAiAgentRequest
{
    public Guid? ProjectId { get; init; }

    public Guid? TaskId { get; init; }

    public required Agent Agent { get; init; }

    public string? ExtraOverride { get; init; }

    public string? Workspace { get; init; }

    public bool Resume { get; init; }
}
