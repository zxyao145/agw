using A2A;
using Agw.Appliaction.Services;
using Agw.Domain.Entities;
using Agw.Domain.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace Agw.A2A;

/// <summary>
/// Service for exposing agents via A2A protocol.
/// </summary>
public class A2AAgentService
{
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly IRepository<Agent> _agentRepository;
    private readonly A2AServerOptions _a2AServerOptions;


    public A2AAgentService(
        AgentRuntimeService agentRuntimeService,
        IRepository<Agent> agentRepository,
        IOptions<A2AServerOptions> a2AServerOptions)
    {
        _agentRuntimeService = agentRuntimeService;
        _agentRepository = agentRepository;
        _a2AServerOptions = a2AServerOptions.Value;
    }

    /// <summary>
    /// Gets an agent by ID for A2A protocol communication.
    /// </summary>
    public async Task<AIAgent?> GetAgentAsync(Guid agentId)
    {
        return await _agentRuntimeService.CreateAiAgentAsync(agentId);
    }

    public async Task<List<AgentCard>> ListAgentCardsAsync()
    {
        var agents = await _agentRepository.ListAsync();
        var agentCards = agents
            .Select(ConvertAgentToCard)
            .ToList();
        return agentCards;
    }

    public async Task<AgentCard?> GetAgentCardAsync(string agentName)
    {
        // Try to parse as GUID first
        Agent? agent = await _agentRepository
                .SingleOrDefaultAsync(a => a.Name == agentName);
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
            Description = string.IsNullOrWhiteSpace(agent.SystemPrompt)
                        ? "An AI agent"
                        : agent.SystemPrompt.Length > 200
                            ? agent.SystemPrompt.Substring(0, 200) + "..."
                            : agent.SystemPrompt,
            Version = "1.0",
            Url = $"{_a2AServerOptions.Prefix}/{agent.Name}​/",
            Capabilities = new AgentCapabilities
            {
                Streaming = true
            }
        };
        return card;
    }
}


