using Agw.Shared.Contracts.Projects;
using Agw.Shared.Extensions;

namespace Agw.Agents.Execution.Agents.Store;

public sealed class AgentSessionStateScope
{
    public AgentSessionStateScope(
        Guid projectContextId,
        Guid projectId,
        string contextId,
        Guid agentId,
        string? agentflowNodeId = null)
    {
        ProjectContextId = projectContextId;
        ProjectId = projectId;
        ContextId = contextId.Trim();
        AgentId = agentId;
        AgentflowNodeId = agentflowNodeId?.Trim() ?? string.Empty;
    }

    public Guid ProjectContextId { get; }

    public Guid ProjectId { get; }

    public string ContextId { get; }

    public Guid AgentId { get; }

    public string AgentflowNodeId { get; }

    internal string CacheKey =>
        string.Join(
            ':',
            ProjectDefaults.GetDefaultProjectIdentifier(ProjectId).Normalize(),
            ContextId,
            AgentId.Normalize(),
            AgentflowNodeId);
}
