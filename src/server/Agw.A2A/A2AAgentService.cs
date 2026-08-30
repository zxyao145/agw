using A2A;
using Agw.Agents.Contracts.Catalog;

namespace Agw.A2A;

/// <summary>
/// Service for exposing agents via A2A protocol.
/// </summary>
public class A2AAgentService
{
    private readonly IAgentCatalogFacade _agents;

    public A2AAgentService(IAgentCatalogFacade agents)
    {
        _agents = agents;
    }

    public async Task<List<AgentCard>> ListAgentCardsAsync()
    {
        var agents = await _agents.ListDiscoverableAsync().ConfigureAwait(false);
        var agentCards = agents.Select(ConvertAgentToCard).ToList();
        return agentCards;
    }

    public async Task<AgentCard?> GetAgentCardAsync(string agentName)
    {
        var agent = await _agents.FindDiscoverableByNameAsync(agentName).ConfigureAwait(false);
        if (agent == null)
        {
            return null;
        }

        var card = ConvertAgentToCard(agent);

        return card;
    }

    private static AgentCard ConvertAgentToCard(AgentDescriptor agent)
    {
        var card = new AgentCard
        {
            Name = agent.Name,
            Description = agent.DiscoveryDescription,
            Version = "1.0",
            Capabilities = new AgentCapabilities { Streaming = true },
        };
        return card;
    }
}
