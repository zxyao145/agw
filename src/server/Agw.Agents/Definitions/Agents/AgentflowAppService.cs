using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Policies;
using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Pagination;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public class AgentflowAppService
{
    private readonly IAgentsDbContext _dbContext;
    private readonly IRepository<ModelProviderRelation> _modelProviderRepository;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;

    public AgentflowAppService(
        IAgentsDbContext dbContext,
        IRepository<ModelProviderRelation> modelProviderRepository,
        TimeProvider timeProvider,
        IUserInfoService userInfoService
    )
    {
        _dbContext = dbContext;
        _modelProviderRepository = modelProviderRepository;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<Agentflow>> ListAsync()
    {
        var ownerUserId = ResolveOwnerUserId();
        return await _dbContext
            .Agentflows.Where(agentflow => agentflow.CreateBy == ownerUserId && agentflow.Enable)
            .ToListAsync();
    }

    public Task<PagedResult<Agentflow>> ListPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    ) =>
        UpdatedTimePagination.ToPagedResultAsync(
            _dbContext.Agentflows.Where(agentflow => agentflow.CreateBy == ResolveOwnerUserId()),
            agentflow => agentflow.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );

    public Task<Agentflow?> GetAsync(Guid id) =>
        _dbContext.Agentflows.FirstOrDefaultAsync(agentflow =>
            agentflow.Id == id && agentflow.CreateBy == ResolveOwnerUserId()
        );

    public async Task<IReadOnlyList<AgentflowNode>> ListNodesAsync(Guid agentflowId)
    {
        if (!await HasVisibleAgentflowAsync(agentflowId).ConfigureAwait(false))
        {
            return [];
        }

        return await _dbContext.AgentflowNodes.Where(x => x.AgentflowId == agentflowId).ToListAsync();
    }

    public async Task<IReadOnlyList<AgentflowEdge>> ListEdgesAsync(Guid agentflowId)
    {
        if (!await HasVisibleAgentflowAsync(agentflowId).ConfigureAwait(false))
        {
            return [];
        }

        return await _dbContext.AgentflowEdges.Where(x => x.AgentflowId == agentflowId).ToListAsync();
    }

    public async Task<Agentflow?> CreateAsync(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        string user
    )
    {
        var behavior = new AgentflowBehavior(agentflow);
        if (!behavior.HasValidName())
        {
            return null;
        }

        var candidateId = agentflow.Id == Guid.Empty ? Guid.CreateVersion7() : agentflow.Id;
        var existingAgents = await ListExistingAgentsAsync(nodes, user);
        var existingAgentflows = await ListExistingAgentflowsAsync(nodes, user, candidateId);
        var existingModelProviderIds = await ListExistingModelProviderIdsAsync(agentflow.SummaryModelProviderId, user);
        var definitionPolicy = new AgentflowDefinitionPolicy();
        var decision = definitionPolicy.Evaluate(
            nodes,
            edges,
            candidateId,
            existingAgents.Keys.ToList(),
            agentflow.SummaryModelProviderId,
            existingModelProviderIds,
            existingAgents,
            existingAgentflows
        );
        if (!behavior.TryApplyGraphDecision(decision))
        {
            return null;
        }

        agentflow.Id = candidateId;
        agentflow.CreateBy = user;
        agentflow.CreateTime = _timeProvider.GetUtcNow();

        foreach (var node in agentflow.Nodes)
        {
            node.CreateBy = user;
            node.CreateTime = agentflow.CreateTime;
        }

        foreach (var edge in agentflow.Edges)
        {
            edge.CreateBy = user;
            edge.CreateTime = agentflow.CreateTime;
        }

        await _dbContext.Agentflows.AddAsync(agentflow);
        await _dbContext.SaveChangesAsync();
        return agentflow;
    }

    public async Task<Agentflow?> UpdateAsync(
        Guid id,
        Action<Agentflow> updateAction,
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        string user
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Agentflows.FirstOrDefaultAsync(agentflow =>
            agentflow.Id == id && agentflow.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);
        var behavior = new AgentflowBehavior(existing);
        if (!behavior.HasValidName())
        {
            return null;
        }

        if (nodes != null && edges != null)
        {
            await _dbContext.AgentflowNodes.Where(node => node.AgentflowId == existing.Id).LoadAsync();
            await _dbContext.AgentflowEdges.Where(edge => edge.AgentflowId == existing.Id).LoadAsync();

            var existingAgents = await ListExistingAgentsAsync(nodes, user);
            var existingAgentflows = await ListExistingAgentflowsAsync(nodes, user, existing.Id);
            var existingModelProviderIds = await ListExistingModelProviderIdsAsync(
                existing.SummaryModelProviderId,
                user
            );
            var definitionPolicy = new AgentflowDefinitionPolicy();
            var decision = definitionPolicy.Evaluate(
                nodes,
                edges,
                existing.Id,
                existingAgents.Keys.ToList(),
                existing.SummaryModelProviderId,
                existingModelProviderIds,
                existingAgents,
                existingAgentflows
            );
            if (!behavior.TryApplyGraphDecision(decision))
            {
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            foreach (var node in existing.Nodes)
            {
                if (node.CreateTime == default)
                {
                    node.CreateBy ??= existing.CreateBy;
                    node.CreateTime = existing.CreateTime == default ? now : existing.CreateTime;
                }
                node.UpdateBy = user;
                node.UpdateTime = now;
            }

            foreach (var edge in existing.Edges)
            {
                if (edge.CreateTime == default)
                {
                    edge.CreateBy ??= existing.CreateBy;
                    edge.CreateTime = existing.CreateTime == default ? now : existing.CreateTime;
                }
                edge.UpdateBy = user;
                edge.UpdateTime = now;
            }
        }

        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task<Agentflow?> UpdateEnabledAsync(
        Guid id,
        bool enable,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Agentflows.FirstOrDefaultAsync(
            agentflow => agentflow.Id == id && agentflow.CreateBy == ownerUserId,
            cancellationToken
        );
        if (existing == null)
        {
            return null;
        }

        existing.Enable = enable;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Agentflows.FirstOrDefaultAsync(agentflow =>
            agentflow.Id == id && agentflow.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return false;
        }

        var currentEdges = await _dbContext.AgentflowEdges.Where(x => x.AgentflowId == existing.Id).ToListAsync();
        foreach (var edge in currentEdges)
        {
            _dbContext.AgentflowEdges.Remove(edge);
        }

        var currentNodes = await _dbContext.AgentflowNodes.Where(x => x.AgentflowId == existing.Id).ToListAsync();
        foreach (var node in currentNodes)
        {
            _dbContext.AgentflowNodes.Remove(node);
        }

        _dbContext.Agentflows.Remove(existing);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ListExistingAgentsAsync(
        IReadOnlyList<AgentflowNode> nodes,
        string ownerUserId
    )
    {
        var agentIds = nodes
            .Where(x => x.Kind == AgentflowNodeKind.Agent)
            .Select(x => x.RelateId)
            .Where(x => x is not null && x.Value != Guid.Empty)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        if (agentIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var existingAgents = await _dbContext
            .Agents.Where(x => agentIds.Contains(x.Id) && x.CreateBy == ownerUserId)
            .ToListAsync();
        return existingAgents.ToDictionary(x => x.Id, x => x.Name);
    }

    private async Task<IReadOnlyCollection<Guid>> ListExistingModelProviderIdsAsync(
        Guid? modelProviderId,
        string ownerUserId
    )
    {
        if (!modelProviderId.HasValue)
        {
            return Array.Empty<Guid>();
        }

        var existing = await _modelProviderRepository.Queryable.FirstOrDefaultAsync(item =>
            item.Id == modelProviderId.Value && item.CreateBy == ownerUserId
        );
        return existing == null ? Array.Empty<Guid>() : [existing.Id];
    }

    private async Task<IReadOnlyCollection<Guid>> ListExistingAgentflowsAsync(
        IReadOnlyList<AgentflowNode> nodes,
        string ownerUserId,
        Guid candidateId
    )
    {
        var agentflowIds = nodes
            .Where(x => x.Kind == AgentflowNodeKind.WorkflowAsAgent)
            .Select(x => x.RelateId)
            .Where(x => x is not null && x.Value != Guid.Empty)
            .Select(x => x!.Value)
            .Where(id => id != candidateId)
            .Distinct()
            .ToArray();
        if (agentflowIds.Length == 0)
        {
            return Array.Empty<Guid>();
        }

        return await _dbContext
            .Agentflows.Where(x => agentflowIds.Contains(x.Id) && x.CreateBy == ownerUserId)
            .Select(x => x.Id)
            .ToArrayAsync();
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;

    private Task<bool> HasVisibleAgentflowAsync(Guid agentflowId)
    {
        var ownerUserId = ResolveOwnerUserId();
        return _dbContext.Agentflows.AnyAsync(agentflow =>
            agentflow.Id == agentflowId && agentflow.CreateBy == ownerUserId
        );
    }
}
