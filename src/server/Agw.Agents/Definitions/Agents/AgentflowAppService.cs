using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Pagination;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public class AgentflowAppService
{
    private readonly IAgentsDbContext _dbContext;
    private readonly AgentflowDefinitionDomainService _definitionDomainService;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;
    private readonly IApplicationLock _applicationLock;

    public AgentflowAppService(
        IAgentsDbContext dbContext,
        AgentflowDefinitionDomainService definitionDomainService,
        TimeProvider timeProvider,
        IUserInfoService userInfoService,
        IApplicationLock? applicationLock = null
    )
    {
        _dbContext = dbContext;
        _definitionDomainService = definitionDomainService;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
        _applicationLock = applicationLock ?? InMemoryApplicationLock.Shared;
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
        string user,
        CancellationToken cancellationToken = default
    )
    {
        var definitionOwner = ResolveOwnerUserId();
        await using var definitionLease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(definitionOwner),
            cancellationToken
        );
        using var mutationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            definitionLease.HandleLostToken
        );
        cancellationToken = mutationCancellation.Token;
        if (!new AgentflowBehavior(agentflow).HasValidName())
        {
            return null;
        }

        agentflow.Id = agentflow.Id == Guid.Empty ? Guid.CreateVersion7() : agentflow.Id;
        var graphDefined = await _definitionDomainService.TryDefineGraphAsync(
            agentflow,
            nodes,
            edges,
            AgentflowConfigurationParser.Parse(nodes, edges),
            cancellationToken
        );
        if (!graphDefined)
        {
            return null;
        }

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

        await _dbContext.Agentflows.AddAsync(agentflow, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return agentflow;
    }

    public async Task<Agentflow?> UpdateAsync(
        Guid id,
        Action<Agentflow> updateAction,
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        string user,
        CancellationToken cancellationToken = default
    )
    {
        var definitionOwner = ResolveOwnerUserId();
        await using var definitionLease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(definitionOwner),
            cancellationToken
        );
        using var mutationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            definitionLease.HandleLostToken
        );
        cancellationToken = mutationCancellation.Token;
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Agentflows.FirstOrDefaultAsync(agentflow =>
            agentflow.Id == id && agentflow.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);
        if (!new AgentflowBehavior(existing).HasValidName())
        {
            return null;
        }

        if (nodes != null && edges != null)
        {
            await _dbContext.AgentflowNodes.Where(node => node.AgentflowId == existing.Id).LoadAsync(cancellationToken);
            await _dbContext.AgentflowEdges.Where(edge => edge.AgentflowId == existing.Id).LoadAsync(cancellationToken);

            var graphDefined = await _definitionDomainService.TryDefineGraphAsync(
                existing,
                nodes,
                edges,
                AgentflowConfigurationParser.Parse(nodes, edges),
                cancellationToken
            );
            if (!graphDefined)
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

        await _dbContext.SaveChangesAsync(cancellationToken);
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

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definitionOwner = ResolveOwnerUserId();
        await using var definitionLease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(definitionOwner),
            cancellationToken
        );
        using var mutationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            definitionLease.HandleLostToken
        );
        cancellationToken = mutationCancellation.Token;
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Agentflows.FirstOrDefaultAsync(agentflow =>
            agentflow.Id == id && agentflow.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return false;
        }

        var currentEdges = await _dbContext
            .AgentflowEdges.Where(x => x.AgentflowId == existing.Id)
            .ToListAsync(cancellationToken);
        foreach (var edge in currentEdges)
        {
            _dbContext.AgentflowEdges.Remove(edge);
        }

        var currentNodes = await _dbContext
            .AgentflowNodes.Where(x => x.AgentflowId == existing.Id)
            .ToListAsync(cancellationToken);
        foreach (var node in currentNodes)
        {
            _dbContext.AgentflowNodes.Remove(node);
        }

        _dbContext.Agentflows.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
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
