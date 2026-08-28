using Agw.Auth.Contracts;
using Agw.Shared.Extensions;

namespace Agw.Agents.Execution.Agents.Store;

public sealed class AgentSessionStateScope
{
    public AgentSessionStateScope(
        Guid projectConversationId,
        Guid projectId,
        string contextId,
        Guid agentId,
        string? agentflowNodeId = null
    )
    {
        ProjectConversationId = projectConversationId;
        ProjectId = projectId;
        ContextId = contextId.Trim();
        AgentId = agentId;
        AgentflowNodeId = agentflowNodeId?.Trim() ?? string.Empty;
    }

    public Guid ProjectConversationId { get; }

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
            AgentflowNodeId
        );
}
