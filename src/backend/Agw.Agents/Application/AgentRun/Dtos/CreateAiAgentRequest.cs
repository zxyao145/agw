using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Application.AgentRun.Dtos;

public sealed class CreateAiAgentRequest
{
    public Guid? ProjectId { get; init; }

    /// <summary>
    /// external agent session id
    /// </summary>
    public Guid? ProviderSessionId { get; init; }

    public required Agent Agent { get; init; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    public Func<string, CancellationToken, ValueTask>? OnExternalSessionStartedAsync { get; init; }

    public bool Resume { get; init; }
}
