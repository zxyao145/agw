namespace Agw.Agents.Application.Persistence;

public interface IAgentDeletionCoordinator
{
    Task<bool> DeleteAsync(Guid agentId, string ownerUserId, CancellationToken cancellationToken = default);
}
