using Microsoft.Agents.AI;

namespace DSystem.Domain.Services;

public interface IProviderSessionState
{
    public void InitializeSessionState(AgentSession session, string contextId, string? sessionId, string? projectId);
}
