using Agw.Auth.Contracts;
using Agw.Shared.Extensions;
using Agw.Shared.Runtime;

namespace Agw.Agents.Execution.Agents.Sessions;

public sealed class AgentSessionStateScope
{
    public AgentSessionStateScope(
        Guid projectConversationId,
        Guid projectId,
        string contextId,
        Guid agentId,
        string? agentflowNodeId = null,
        int? generation = null
    )
    {
        ProjectConversationId = projectConversationId;
        ProjectId = projectId;
        Generation = generation ?? ExecutionContextSlot.FindBound(projectId, contextId)?.Generation ?? 0;
        ContextId = contextId.Trim();
        AgentId = agentId;
        AgentflowNodeId = agentflowNodeId?.Trim() ?? string.Empty;
    }

    public Guid ProjectConversationId { get; }

    public int Generation { get; }

    public Guid ProjectId { get; }

    public string ContextId { get; }

    public Guid AgentId { get; }

    public string AgentflowNodeId { get; }

    internal string CacheKey =>
        string.Join(
            ':',
            UserInfoUtil.RequiredUserId,
            ProjectDefaults.GetDefaultProjectIdentifier(ProjectId).Normalize(),
            ContextId,
            AgentId.Normalize(),
            AgentflowNodeId,
            Generation
        );
}
