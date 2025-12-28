using A2A;
using DSystem.A2A;
using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace DSystem.Domain.Services;

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

    public async Task<AgentCard?> GetAgentCardAsync(string agentId)
    {
        // Try to parse as GUID first
        Agent? agent;
        if (Guid.TryParse(agentId, out var guid))
        {
            agent = await _agentRepository.GetByIdAsync(guid);
        }
        else
        {
            // Treat as agent name
            agent = (await _agentRepository
                .ListAsync(a => a.Name.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                ).FirstOrDefault();
        }

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
            Url = $"{_a2AServerOptions.Prefix}/{agent.Id}​/",
            Capabilities = new AgentCapabilities
            {
                Streaming = true
            }
        };
        return card;
    }
}
