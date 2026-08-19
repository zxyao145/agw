namespace Agw.Shared.Contracts.Projects;

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
