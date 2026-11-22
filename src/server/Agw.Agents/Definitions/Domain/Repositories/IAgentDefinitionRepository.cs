namespace Agw.Agents.Definitions.Domain.Repositories;

/// <summary>
/// <para>读取当前所有者的 Agent、MCP Server 与 Agentflow 定义事实，供跨越单个定义的规则使用。</para>
/// <para>Reads the current owner's agent, MCP server and Agentflow definition facts for rules that span definitions.</para>
/// </summary>
public interface IAgentDefinitionRepository
{
    Task<bool> AgentNameExistsAsync(string name);

    Task<IReadOnlySet<Guid>> FilterOwnedAgentIdsAsync(IReadOnlyCollection<Guid> agentIds);

    Task<IReadOnlySet<Guid>> FilterOwnedMcpServerIdsAsync(IReadOnlyCollection<Guid> serverIds);

    Task<IReadOnlySet<Guid>> FilterOwnedAgentflowIdsAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyDictionary<Guid, int>> ListOwnedAgentflowPublishedRevisionsAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>> LoadPublishedAgentflowReferencesAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    );

    Task<IReadOnlySet<Guid>> FilterStructuredResultAgentIdsAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken cancellationToken
    );
}
