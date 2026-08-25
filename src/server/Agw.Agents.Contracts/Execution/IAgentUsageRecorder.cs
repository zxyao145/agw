namespace Agw.Agents.Contracts.Execution;

public interface IAgentUsageRecorder
{
    Task AddAsync(
        Guid projectId,
        string contextId,
        string agentName,
        ProjectContextUsage usage,
        CancellationToken cancellationToken = default
    );
}
