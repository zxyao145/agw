using A2A;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.A2A;

/// <summary>
/// Service for exposing agents via A2A protocol.
/// </summary>
public class A2AAgentService
{
    private readonly IRepository<Agent> _agentRepository;

    public A2AAgentService(IRepository<Agent> agentRepository)
    {
        _agentRepository = agentRepository;
    }

    public async Task<List<AgentCard>> ListAgentCardsAsync()
    {
        var agents = await _agentRepository.ListAsync();
        var agentCards = agents.Select(ConvertAgentToCard).ToList();
        return agentCards;
    }

    public async Task<AgentCard?> GetAgentCardAsync(string agentName)
    {
        // Try to parse as GUID first
        Agent? agent = await _agentRepository.SingleOrDefaultAsync(a => a.Name == agentName);
        if (agent == null)
        {
            return null;
        }

        var card = ConvertAgentToCard(agent);

        return card;
    }

    private AgentCard ConvertAgentToCard(Agent agent)
    {
        var card = new AgentCard
        {
            Name = agent.Name,
            Description =
                string.IsNullOrWhiteSpace(agent.SystemPrompt) ? "An AI agent"
                : agent.SystemPrompt.Length > 200 ? agent.SystemPrompt.Substring(0, 200) + "..."
                : agent.SystemPrompt,
            Version = "1.0",
            Capabilities = new AgentCapabilities { Streaming = true },
        };
        return card;
    }
}
