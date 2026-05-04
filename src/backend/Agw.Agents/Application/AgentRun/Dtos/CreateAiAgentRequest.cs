namespace Agw.Agents.Application.AgentRun.Dtos;

public sealed class CreateAiAgentRequest
{
    public Guid? ProjectId { get; init; }

    public Guid? TaskId { get; init; }

    public Guid? ProviderSessionId { get; init; }

    public required Agent Agent { get; init; }

    public string? ExtraOverride { get; init; }

    public string? Workspace { get; init; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    public Func<string, CancellationToken, ValueTask>? OnExternalSessionStartedAsync { get; init; }

    public bool Resume { get; init; }
}
