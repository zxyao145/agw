using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Contracts.Tasks;

public interface ITaskSessionBindingService
{
    Task<TaskSessionBinding?> GetAsync(
        Guid taskId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default);

    Task<TaskSessionBinding> UpsertAsync(
        Guid taskId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default);
}
