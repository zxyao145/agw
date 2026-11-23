using System.Linq.Expressions;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Domain;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Pagination;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public sealed record AgentflowDetail(Agentflow Agentflow, int Revision, AgentflowDefinition Definition);

public class AgentflowAppService
{
    private readonly IAgentsDbContext _dbContext;
    private readonly ITransaction _transaction;
    private readonly AgentflowDefinitionDomainService _definitionDomainService;
    private readonly IAgentflowRevisionUsageReader _revisionUsageReader;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;
    private readonly IApplicationLock _applicationLock;

    public AgentflowAppService(
        IAgentsDbContext dbContext,
        ITransaction transaction,
        AgentflowDefinitionDomainService definitionDomainService,
        IAgentflowRevisionUsageReader revisionUsageReader,
        TimeProvider timeProvider,
        IUserInfoService userInfoService,
        IApplicationLock? applicationLock = null
    )
    {
        _dbContext = dbContext;
        _transaction = transaction;
        _definitionDomainService = definitionDomainService;
        _revisionUsageReader = revisionUsageReader;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
        _applicationLock = applicationLock ?? InMemoryApplicationLock.Shared;
    }

    public async Task<IReadOnlyList<Agentflow>> ListAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        return await _dbContext
            .Agentflows.AsNoTracking()
            .Where(agentflow =>
                agentflow.CreateBy == ownerUserId && agentflow.Enable && agentflow.PublishedRevision > 0
            )
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<PagedResult<Agentflow>> ListPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    ) =>
        UpdatedTimePagination.ToPagedResultAsync(
            _dbContext.Agentflows.AsNoTracking().Where(agentflow => agentflow.CreateBy == ResolveOwnerUserId()),
            agentflow => agentflow.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );

    public async Task<AgentflowDetail> GetDetailAsync(
        Guid id,
        int revision,
        CancellationToken cancellationToken = default
    )
    {
        if (revision < 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var ownerUserId = ResolveOwnerUserId();
        var agentflow = await FindVisibleAsync(id, ownerUserId, cancellationToken).ConfigureAwait(false);
        var definition = await ReadDefinitionAsync(id, revision, cancellationToken).ConfigureAwait(false);
        return new AgentflowDetail(agentflow, revision, definition);
    }

    public async Task<IReadOnlyList<AgentflowRevision>> ListRevisionsAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        _ = await FindVisibleAsync(id, ownerUserId, cancellationToken).ConfigureAwait(false);
        return await _dbContext
            .AgentflowRevisions.AsNoTracking()
            .Where(revision => revision.AgentflowId == id)
            .OrderByDescending(revision => revision.Revision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Agentflow> CreateAsync(
        string name,
        string? description,
        AgentflowDefinition definition,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        await using var definitionLease = await AgentDefinitionLock
            .AcquireScopeAsync(_applicationLock, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken = definitionLease.Token;
        var agentflow = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = description,
            CreateBy = ownerUserId,
            CreateTime = _timeProvider.GetUtcNow(),
        };
        new AgentflowBehavior(agentflow).StartRevisions();

        var problems = await _definitionDomainService
            .ValidateAsync(null, name, definition, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfInvalid(problems);

        _dbContext.Agentflows.Add(agentflow);
        _dbContext.AgentflowRevisions.Add(CreateRevision(agentflow.Id, 1, ownerUserId, agentflow.CreateTime));
        AddDefinition(definition, agentflow.Id, 1, ownerUserId, agentflow.CreateTime);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return agentflow;
    }

    public async Task<Agentflow> SaveRevisionAsync(
        Guid agentflowId,
        int baseRevision,
        string name,
        string? description,
        AgentflowDefinition definition,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        await using var definitionLease = await AgentDefinitionLock
            .AcquireScopeAsync(_applicationLock, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken = definitionLease.Token;
        var existing = await FindVisibleAsync(agentflowId, ownerUserId, cancellationToken).ConfigureAwait(false);
        var behavior = new AgentflowBehavior(existing);
        if (!behavior.TryAdvanceRevision(baseRevision))
        {
            throw new AgwException(ErrorCodes.AgentflowRevisionConflict);
        }

        var nextRevision = existing.LatestRevision;
        var problems = await _definitionDomainService
            .ValidateAsync(agentflowId, name, definition, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfInvalid(problems);

        var now = _timeProvider.GetUtcNow();
        await PersistRevisionAsync(
                agentflowId,
                nextRevision,
                baseRevision,
                name,
                description,
                definition,
                ownerUserId,
                now,
                cancellationToken
            )
            .ConfigureAwait(false);

        existing.LatestRevision = nextRevision;
        existing.Name = name;
        existing.Description = description;
        existing.UpdateBy = ownerUserId;
        existing.UpdateTime = now;
        return existing;
    }

    private async Task PersistRevisionAsync(
        Guid agentflowId,
        int nextRevision,
        int baseRevision,
        string name,
        string? description,
        AgentflowDefinition definition,
        string ownerUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        await _transaction.TransactionBeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UpdateLatestRevisionAsync(
                    agentflowId,
                    nextRevision,
                    baseRevision,
                    name,
                    description,
                    ownerUserId,
                    now,
                    cancellationToken
                )
                .ConfigureAwait(false);
            _dbContext.AgentflowRevisions.Add(CreateRevision(agentflowId, nextRevision, ownerUserId, now));
            AddDefinition(definition, agentflowId, nextRevision, ownerUserId, now);
            await _transaction.TransactionCommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_transaction.HasActiveTransaction)
            {
                await _transaction.TransactionRollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task UpdateLatestRevisionAsync(
        Guid agentflowId,
        int nextRevision,
        int baseRevision,
        string name,
        string? description,
        string ownerUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var affected = await _dbContext
            .Agentflows.Where(agentflow =>
                agentflow.Id == agentflowId
                && agentflow.CreateBy == ownerUserId
                && agentflow.LatestRevision == baseRevision
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(agentflow => agentflow.LatestRevision, nextRevision)
                        .SetProperty(agentflow => agentflow.Name, name)
                        .SetProperty(agentflow => agentflow.Description, description)
                        .SetProperty(agentflow => agentflow.UpdateBy, ownerUserId)
                        .SetProperty(agentflow => agentflow.UpdateTime, now),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (affected == 0)
        {
            throw new AgwException(ErrorCodes.AgentflowRevisionConflict);
        }
    }

    public async Task<Agentflow> PublishAsync(
        Guid agentflowId,
        int revision,
        CancellationToken cancellationToken = default
    )
    {
        if (revision < 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var ownerUserId = ResolveOwnerUserId();
        await using var definitionLease = await AgentDefinitionLock
            .AcquireScopeAsync(_applicationLock, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken = definitionLease.Token;
        var agentflow = await FindVisibleAsync(agentflowId, ownerUserId, cancellationToken).ConfigureAwait(false);
        var definition = await ReadDefinitionAsync(agentflowId, revision, cancellationToken).ConfigureAwait(false);
        var problems = await _definitionDomainService
            .ValidateAsync(agentflowId, agentflow.Name, definition, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfInvalid(problems);

        var now = _timeProvider.GetUtcNow();
        new AgentflowBehavior(agentflow).Publish(revision);
        await _dbContext
            .Agentflows.Where(row => row.Id == agentflowId && row.CreateBy == ownerUserId)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.PublishedRevision, agentflow.PublishedRevision)
                        .SetProperty(row => row.UpdateBy, ownerUserId)
                        .SetProperty(row => row.UpdateTime, now),
                cancellationToken
            )
            .ConfigureAwait(false);
        agentflow.UpdateBy = ownerUserId;
        agentflow.UpdateTime = now;
        return agentflow;
    }

    public Task<IReadOnlyList<AgentflowProblem>> ValidateAsync(
        Guid? agentflowId,
        string name,
        AgentflowDefinition definition,
        CancellationToken cancellationToken = default
    ) => _definitionDomainService.ValidateAsync(agentflowId, name, definition, cancellationToken);

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
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        await using var definitionLease = await AgentDefinitionLock
            .AcquireScopeAsync(_applicationLock, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken = definitionLease.Token;
        var existing = await _dbContext.Agentflows.FirstOrDefaultAsync(
            agentflow => agentflow.Id == id && agentflow.CreateBy == ownerUserId,
            cancellationToken
        );
        if (existing is null)
        {
            return false;
        }

        await ThrowIfPinnedAsync(id, cancellationToken).ConfigureAwait(false);
        await RemoveDefinitionRowsAsync(id, cancellationToken).ConfigureAwait(false);
        _dbContext.Agentflows.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ThrowIfPinnedAsync(Guid agentflowId, CancellationToken cancellationToken)
    {
        var pinnedRevisions = await _revisionUsageReader
            .ListPinnedRevisionsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (pinnedRevisions.Any(revision => revision.AgentflowId == agentflowId))
        {
            throw new AgwException(ErrorCodes.AgentflowInUse);
        }
    }

    private async Task RemoveDefinitionRowsAsync(Guid agentflowId, CancellationToken cancellationToken)
    {
        await RemoveRowsAsync(
                _dbContext.AgentflowBlockMembers,
                member => member.AgentflowId == agentflowId,
                cancellationToken
            )
            .ConfigureAwait(false);
        await RemoveRowsAsync(_dbContext.AgentflowEdges, edge => edge.AgentflowId == agentflowId, cancellationToken)
            .ConfigureAwait(false);
        await RemoveRowsAsync(_dbContext.AgentflowNodes, node => node.AgentflowId == agentflowId, cancellationToken)
            .ConfigureAwait(false);
        await RemoveRowsAsync(
                _dbContext.AgentflowRevisions,
                revision => revision.AgentflowId == agentflowId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task RemoveRowsAsync<TEntity>(
        DbSet<TEntity> rows,
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken
    )
        where TEntity : class =>
        rows.RemoveRange(await rows.Where(predicate).ToListAsync(cancellationToken).ConfigureAwait(false));

    private async Task<Agentflow> FindVisibleAsync(Guid id, string ownerUserId, CancellationToken cancellationToken)
    {
        var agentflow = await _dbContext
            .Agentflows.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id && row.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        return agentflow ?? throw new AgwException(ErrorCodes.ResourceNotFound);
    }

    private async Task<AgentflowDefinition> ReadDefinitionAsync(
        Guid agentflowId,
        int revision,
        CancellationToken cancellationToken
    )
    {
        var revisionExists = await _dbContext
            .AgentflowRevisions.AnyAsync(
                row => row.AgentflowId == agentflowId && row.Revision == revision,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!revisionExists)
        {
            throw new AgwException(ErrorCodes.AgentflowRevisionNotFound);
        }

        var nodes = await _dbContext
            .AgentflowNodes.AsNoTracking()
            .Where(node => node.AgentflowId == agentflowId && node.AgentflowRevision == revision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var edges = await _dbContext
            .AgentflowEdges.AsNoTracking()
            .Where(edge => edge.AgentflowId == agentflowId && edge.AgentflowRevision == revision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var members = await _dbContext
            .AgentflowBlockMembers.AsNoTracking()
            .Where(member => member.AgentflowId == agentflowId && member.AgentflowRevision == revision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return AgentflowDefinitionMapper.FromEntities(nodes, edges, members);
    }

    private void AddDefinition(
        AgentflowDefinition definition,
        Guid agentflowId,
        int revision,
        string ownerUserId,
        DateTimeOffset now
    )
    {
        AddNodes(definition.Nodes, agentflowId, revision, ownerUserId, now);
        AddEdges(definition.Edges, agentflowId, revision, ownerUserId, now);
        AddMembers(definition.Members, agentflowId, revision, ownerUserId, now);
    }

    private void AddNodes(
        IReadOnlyList<AgentflowNode> nodes,
        Guid agentflowId,
        int revision,
        string ownerUserId,
        DateTimeOffset now
    ) =>
        _dbContext.AgentflowNodes.AddRange(
            nodes.Select(node => new AgentflowNode
            {
                AgentflowId = agentflowId,
                AgentflowRevision = revision,
                NodeId = node.NodeId,
                Kind = node.Kind,
                RelateId = node.RelateId,
                Name = node.Name,
                PositionX = node.PositionX,
                PositionY = node.PositionY,
                RoutingStrategy = node.RoutingStrategy,
                SummaryModelProviderId = node.SummaryModelProviderId,
                Instructions = node.Instructions,
                ConfigJson = node.ConfigJson,
                CreateBy = ownerUserId,
                CreateTime = now,
            })
        );

    private void AddEdges(
        IReadOnlyList<AgentflowEdge> edges,
        Guid agentflowId,
        int revision,
        string ownerUserId,
        DateTimeOffset now
    ) =>
        _dbContext.AgentflowEdges.AddRange(
            edges.Select(edge => new AgentflowEdge
            {
                AgentflowId = agentflowId,
                AgentflowRevision = revision,
                EdgeId = edge.EdgeId,
                SourceNodeId = edge.SourceNodeId,
                TargetNodeId = edge.TargetNodeId,
                IsFanInBarrier = edge.IsFanInBarrier,
                IsSwitchDefault = edge.IsSwitchDefault,
                SwitchCaseOrder = edge.SwitchCaseOrder,
                ChoiceId = edge.ChoiceId,
                Label = edge.Label,
                ConditionJson = edge.ConditionJson,
                CreateBy = ownerUserId,
                CreateTime = now,
            })
        );

    private void AddMembers(
        IReadOnlyList<AgentflowBlockMember> members,
        Guid agentflowId,
        int revision,
        string ownerUserId,
        DateTimeOffset now
    ) =>
        _dbContext.AgentflowBlockMembers.AddRange(
            members.Select(member => new AgentflowBlockMember
            {
                AgentflowId = agentflowId,
                AgentflowRevision = revision,
                NodeId = member.NodeId,
                MemberId = member.MemberId,
                Kind = member.Kind,
                RelateId = member.RelateId,
                Name = member.Name,
                Instructions = member.Instructions,
                SortOrder = member.SortOrder,
                IsManager = member.IsManager,
                CreateBy = ownerUserId,
                CreateTime = now,
            })
        );

    private static AgentflowRevision CreateRevision(
        Guid agentflowId,
        int revision,
        string ownerUserId,
        DateTimeOffset now
    ) =>
        new()
        {
            AgentflowId = agentflowId,
            Revision = revision,
            CreateBy = ownerUserId,
            CreateTime = now,
        };

    private static void ThrowIfInvalid(IReadOnlyList<AgentflowProblem> problems)
    {
        if (problems.Count > 0)
        {
            throw new AgwException(ErrorCodes.AgentflowDefinitionInvalid);
        }
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
