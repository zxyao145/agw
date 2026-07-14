using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Contracts.Tasks;

public interface IAgentUsageRecorder
{
    Task AddAsync(
        Guid projectId,
        string contextId,
        string agentName,
        ProjectContextUsage usage,
        CancellationToken cancellationToken = default);
}
