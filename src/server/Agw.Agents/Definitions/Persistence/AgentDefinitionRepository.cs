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

    public async Task<IReadOnlyDictionary<Guid, int>> ListOwnedAgentflowPublishedRevisionsAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var rows = await _dbContext
            .Agentflows.AsNoTracking()
            .Where(agentflow => agentflowIds.Contains(agentflow.Id) && agentflow.CreateBy == ownerUserId)
            .Select(agentflow => new { agentflow.Id, agentflow.PublishedRevision })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(row => row.Id, row => row.PublishedRevision);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>> LoadPublishedAgentflowReferencesAsync(
        IReadOnlyCollection<Guid> agentflowIds,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var references = new Dictionary<Guid, IReadOnlyCollection<Guid>>();
        var pending = agentflowIds.Distinct().ToArray();
        while (pending.Length > 0)
        {
            var flows = await _dbContext
                .Agentflows.AsNoTracking()
                .Where(agentflow => pending.Contains(agentflow.Id) && agentflow.CreateBy == ownerUserId)
                .Select(agentflow => new { agentflow.Id, agentflow.PublishedRevision })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var flowIdsWithoutPublishedRevision = pending.Except(flows.Select(flow => flow.Id)).ToArray();
            foreach (var id in flowIdsWithoutPublishedRevision)
            {
                references[id] = [];
            }

            var next = new HashSet<Guid>();
            foreach (
                var revisionGroup in flows
                    .Where(flow => flow.PublishedRevision > 0)
                    .GroupBy(flow => flow.PublishedRevision)
            )
            {
                var ids = revisionGroup.Select(flow => flow.Id).ToArray();
                var nodes = await _dbContext
                    .AgentflowNodes.AsNoTracking()
                    .Where(node =>
                        ids.Contains(node.AgentflowId)
                        && node.AgentflowRevision == revisionGroup.Key
                        && node.Kind == AgentflowNodeKind.WorkflowAsAgent
                        && node.RelateId.HasValue
                    )
                    .Select(node => new { node.AgentflowId, TargetId = node.RelateId!.Value })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var members = await _dbContext
                    .AgentflowBlockMembers.AsNoTracking()
                    .Where(member =>
                        ids.Contains(member.AgentflowId)
                        && member.AgentflowRevision == revisionGroup.Key
                        && member.Kind == AgentflowNodeKind.WorkflowAsAgent
                    )
                    .Select(member => new { member.AgentflowId, TargetId = member.RelateId })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var id in ids)
                {
                    var directReferences = nodes
                        .Where(node => node.AgentflowId == id)
                        .Select(node => node.TargetId)
                        .Concat(members.Where(member => member.AgentflowId == id).Select(member => member.TargetId))
                        .Distinct()
                        .ToArray();
                    references[id] = directReferences;
                    foreach (var targetId in directReferences.Where(targetId => !references.ContainsKey(targetId)))
                    {
                        next.Add(targetId);
                    }
                }
            }

            pending = next.ToArray();
        }

        return references;
    }

    public async Task<IReadOnlySet<Guid>> FilterStructuredResultAgentIdsAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        return await _dbContext
            .Agents.AsNoTracking()
            .Where(agent =>
                agentIds.Contains(agent.Id)
                && agent.CreateBy == ownerUserId
                && agent.EnableSummary
                && agent.ResponseSchema != null
                && agent.ResponseSchema != ""
            )
            .Select(agent => agent.Id)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
