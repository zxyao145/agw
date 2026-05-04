using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Contracts.Tasks;

public interface IProjectTaskSessionBindingService
{
    Task<ProjectTaskSessionBinding?> GetAsync(
        Guid taskId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default);

    Task<ProjectTaskSessionBinding> UpsertAsync(
        Guid taskId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default);
}
