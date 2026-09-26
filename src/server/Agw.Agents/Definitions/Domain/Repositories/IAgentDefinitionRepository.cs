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

    Task<IReadOnlyDictionary<Guid, string>> ListOwnedAgentNamesAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken cancellationToken
    );

    Task<IReadOnlySet<Guid>> FilterOwnedAgentflowIdsAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// <para>从给定 Agentflow 出发，沿嵌套 Agentflow 节点逐层读取所有者的引用关系，返回每个 Agentflow 直接引用的 Agentflow。</para>
    /// <para>Walks the owner's nested Agentflow nodes level by level from the given Agentflows and returns each one's direct Agentflow references.</para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>> LoadNestedAgentflowReferencesAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    );
}
