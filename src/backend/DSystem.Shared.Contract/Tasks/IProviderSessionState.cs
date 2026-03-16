using Microsoft.Agents.AI;

namespace DSystem.Shared.Tasks;

public interface IProviderSessionState
{
    public void InitializeSessionState(AgentSession session, string contextId, string? sessionId, string? projectId);
}
