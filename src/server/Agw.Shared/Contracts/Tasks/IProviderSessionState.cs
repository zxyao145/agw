using Microsoft.Agents.AI;

namespace Agw.Shared.Contracts.Tasks;

public interface IProviderSessionState
{
    void InitializeSessionState(AgentSession session, string contextId, Guid projectId);

    bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
    {
        projectId = Guid.Empty;
        contextId = string.Empty;
        return false;
    }
}
