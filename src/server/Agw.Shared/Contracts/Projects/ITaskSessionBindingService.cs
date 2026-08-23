using Agw.Shared.Data.Entities.Projects;

namespace Agw.Shared.Contracts.Projects;

public interface ITaskSessionBindingService
{
    Task<TaskSessionBinding?> GetAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default
    );

    Task<TaskSessionBinding> UpsertAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default
    );

    Task DeleteByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
