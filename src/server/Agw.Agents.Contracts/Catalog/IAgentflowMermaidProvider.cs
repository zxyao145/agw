namespace Agw.Agents.Contracts.Catalog;

public interface IAgentflowMermaidProvider
{
    Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default);
}
