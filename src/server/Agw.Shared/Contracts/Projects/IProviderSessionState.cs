using Microsoft.Agents.AI;

namespace Agw.Shared.Contracts.Projects;

public interface IProviderSessionState
{
    void InitializeSessionState(AgentSession session, string contextId, Guid projectId);

    void InitializeSessionState(
        AgentSession session,
        string contextId,
        Guid projectId,
        string historyScope);

    bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
    {
        projectId = Guid.Empty;
        contextId = string.Empty;
        return false;
    }
}
