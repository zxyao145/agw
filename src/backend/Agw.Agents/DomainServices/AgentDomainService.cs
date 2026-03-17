using Agw.Domain.Entities;
using Agw.Shared.Enums;
using Agw.Domain.Repositories;
using System.Linq.Expressions;

namespace Agw.Domain.Services;

public class AgentDomainService
{
    private readonly IRepository<Agent> _repository;
    private readonly IRepository<ModelProvider> _modelProviderRepository;
    private readonly IRepository<McpToolServer> _mcpToolServerRepository;
    private readonly IRepository<AgentMcpToolServer> _agentMcpToolServerRepository;
    private readonly IUnitOfWork _unitOfWork;

    public AgentDomainService(
        IRepository<Agent> repository,
        IRepository<ModelProvider> modelProviderRepository,
        IRepository<McpToolServer> mcpToolServerRepository,
        IRepository<AgentMcpToolServer> agentMcpToolServerRepository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _modelProviderRepository = modelProviderRepository;
        _mcpToolServerRepository = mcpToolServerRepository;
        _agentMcpToolServerRepository = agentMcpToolServerRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Agent?> CreateAsync(Agent agent, IEnumerable<Guid>? mcpToolServerIds, string user)
    {
        // Validate ModelProviderId based on AgentType
        if (agent.Type == AgentType.System)
        {
            // System agents require a valid ModelProviderId
            if (!agent.ModelProviderId.HasValue)
            {
                throw new InvalidOperationException("System agents must have a ModelProviderId.");
            }

            var modelProvider = await _modelProviderRepository.GetByIdAsync(agent.ModelProviderId.Value);
            if (modelProvider == null)
            {
                return null;
            }
        }
        else if (agent.Type == AgentType.External)
        {
            // External agents can have optional ModelProviderId
            if (agent.ModelProviderId.HasValue)
            {
                var modelProvider = await _modelProviderRepository.GetByIdAsync(agent.ModelProviderId.Value);
                if (modelProvider == null)
                {
                    return null;
                }
            }
        }

        agent.Id = agent.Id == Guid.Empty ? Guid.NewGuid() : agent.Id;
        if (string.IsNullOrWhiteSpace(agent.Name))
        {
            agent.Name = agent.Id.ToString();
        }
        agent.CreateBy = user;
        agent.CreateTime = DateTime.UtcNow;
        await _repository.AddAsync(agent);
        await SyncMcpToolServerRelationsAsync(agent.Id, mcpToolServerIds);
        await _unitOfWork.SaveChangesAsync();
        return agent;
    }

    public async Task<Agent?> UpdateAsync(Guid id, Action<Agent> updateAction, IEnumerable<Guid>? mcpToolServerIds, string user)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        // For External agents, only allow Description, Extra, and ModelProviderId to be modified
        if (existing.Type == AgentType.External)
        {
            // Store original values of non-editable fields
            var originalId = existing.Id;
            var originalName = existing.Name;
            var originalSystemPrompt = existing.SystemPrompt;
            var originalTools = existing.Tools;
            var originalType = existing.Type;

            updateAction(existing);

            // Restore non-editable fields
            existing.Id = originalId;
            existing.Name = originalName;
            existing.SystemPrompt = originalSystemPrompt;
            existing.Tools = originalTools;
            existing.Type = originalType;

            // Validate ModelProviderId if it was changed
            if (existing.ModelProviderId.HasValue)
            {
                var modelProvider = await _modelProviderRepository.GetByIdAsync(existing.ModelProviderId.Value);
                if (modelProvider == null)
                {
                    return null;
                }
            }
        }
        else
        {
            updateAction(existing);

            // Validate ModelProviderId for System agents (required)
            if (existing.Type == AgentType.System)
            {
                if (!existing.ModelProviderId.HasValue)
                {
                    throw new InvalidOperationException("System agents must have a ModelProviderId.");
                }

                var modelProvider = await _modelProviderRepository.GetByIdAsync(existing.ModelProviderId.Value);
                if (modelProvider == null)
                {
                    return null;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(existing.Name))
        {
            existing.Name = existing.Id.ToString();
        }

        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
        _repository.Update(existing);
        await SyncMcpToolServerRelationsAsync(existing.Id, mcpToolServerIds);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _repository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public Task<IReadOnlyList<Agent>> ListAsync(Expression<Func<Agent, bool>>? predicate = null) =>
        _repository.ListAsync(predicate, x => x.AgentMcpToolServers);

    public async Task<Agent?> GetAsync(Guid id)
    {
        var matches = await _repository.ListAsync(x => x.Id == id, x => x.AgentMcpToolServers);
        return matches.FirstOrDefault();
    }

    private async Task SyncMcpToolServerRelationsAsync(Guid agentId, IEnumerable<Guid>? mcpToolServerIds)
    {
        var existingLinks = await _agentMcpToolServerRepository.ListAsync(x => x.AgentId == agentId);
        foreach (var link in existingLinks)
        {
            _agentMcpToolServerRepository.Remove(link);
        }

        var requestedIds = (mcpToolServerIds ?? [])
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var existingServers = await _mcpToolServerRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var serverId in existingServers.Select(x => x.Id))
        {
            await _agentMcpToolServerRepository.AddAsync(new AgentMcpToolServer
            {
                AgentId = agentId,
                McpToolServerId = serverId
            });
        }
    }
}
