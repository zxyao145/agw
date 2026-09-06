using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Application;

public interface ITaskSessionBindingService
{
    Task<TaskSessionBinding?> GetAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    );

    Task<TaskSessionBinding> UpsertAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    );
}
