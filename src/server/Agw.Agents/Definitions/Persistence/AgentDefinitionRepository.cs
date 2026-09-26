using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Domain.Repositories;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Persistence;

public sealed class AgentDefinitionRepository : IAgentDefinitionRepository
{
    private readonly IAgentsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public AgentDefinitionRepository(IAgentsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public Task<bool> AgentNameExistsAsync(string name)
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return _dbContext.Agents.AnyAsync(agent => agent.CreateBy == ownerUserId && agent.Name == name);
    }

    public async Task<IReadOnlySet<Guid>> FilterOwnedAgentIdsAsync(IReadOnlyCollection<Guid> agentIds)
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return await _dbContext
            .Agents.AsNoTracking()
            .Where(agent => agentIds.Contains(agent.Id) && agent.CreateBy == ownerUserId)
            .Select(agent => agent.Id)
            .ToHashSetAsync();
    }

    public async Task<IReadOnlySet<Guid>> FilterOwnedMcpServerIdsAsync(IReadOnlyCollection<Guid> serverIds)
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return await _dbContext
            .McpToolServers.Where(server => serverIds.Contains(server.Id) && server.CreateBy == ownerUserId)
            .Select(server => server.Id)
            .ToHashSetAsync();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ListOwnedAgentNamesAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var agents = await _dbContext
            .Agents.Where(agent => agentIds.Contains(agent.Id) && agent.CreateBy == ownerUserId)
            .ToListAsync(cancellationToken);
        return agents.ToDictionary(agent => agent.Id, agent => agent.Name);
    }

    public async Task<IReadOnlySet<Guid>> FilterOwnedAgentflowIdsAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return await _dbContext
            .Agentflows.Where(agentflow => agentflowIds.Contains(agentflow.Id) && agentflow.CreateBy == ownerUserId)
            .Select(agentflow => agentflow.Id)
            .ToHashSetAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>> LoadNestedAgentflowReferencesAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var references = new Dictionary<Guid, IReadOnlyCollection<Guid>>();
        var pending = agentflowIds.Distinct().ToArray();
        var owned = _dbContext.Agentflows.Where(flow => flow.CreateBy == ownerUserId).Select(flow => flow.Id);
        while (pending.Length > 0)
        {
            var batch = pending;
            var links = await _dbContext
                .AgentflowNodes.AsNoTracking()
                .Where(node =>
                    batch.Contains(node.AgentflowId)
                    && owned.Contains(node.AgentflowId)
                    && node.Kind == AgentflowNodeKind.WorkflowAsAgent
                    && node.RelateId.HasValue
                )
                .Select(node => new { node.AgentflowId, Target = node.RelateId!.Value })
                .ToListAsync(cancellationToken);
            foreach (var id in batch)
            {
                references[id] = links
                    .Where(link => link.AgentflowId == id)
                    .Select(link => link.Target)
                    .Distinct()
                    .ToArray();
            }
            pending = links.Select(link => link.Target).Where(id => !references.ContainsKey(id)).Distinct().ToArray();
        }
        return references;
    }
}
