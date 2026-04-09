using Microsoft.Agents.AI;

namespace Agw.Shared.Tasks;

public interface IProviderSessionState
{
    public void InitializeSessionState(AgentSession session, string contextId, string? taskId, Guid projectId);
}
