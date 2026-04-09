using Microsoft.Agents.AI;

namespace Agw.Shared.Contracts.Tasks;

public interface IProviderSessionState
{
    void InitializeSessionState(AgentSession session, string contextId, string? taskId, Guid projectId);
}
