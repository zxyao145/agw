namespace Agw.Agents.Contracts.Catalog;

public interface IAgentflowMermaidProvider
{
    Task<string?> GetMermaidAsync(
        Guid agentflowId,
        int agentflowRevision,
        CancellationToken cancellationToken = default
    );
}
