using Agw.Agents.Application.Persistence;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Persistence;

public sealed class AgentflowRevisionSnapshotReader : IAgentflowRevisionSnapshotReader
{
    private readonly IAgentsDbContext _dbContext;

    public AgentflowRevisionSnapshotReader(IAgentsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AgentflowRevisionSnapshot> CreateRevisionSnapshotAsync(
        Guid agentflowId,
        int requestedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = UserInfoUtil.RequiredUserId;
        if (requestedRevision < 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var root = await _dbContext
            .Agentflows.AsNoTracking()
            .FirstOrDefaultAsync(
                agentflow => agentflow.Id == agentflowId && agentflow.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (root is null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        var rootRevision = requestedRevision == 0 ? root.PublishedRevision : requestedRevision;
        if (rootRevision == 0)
        {
            throw new AgwException(ErrorCodes.AgentflowNotPublished);
        }

        if (!await RevisionExistsAsync(agentflowId, rootRevision, cancellationToken).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.AgentflowRevisionNotFound);
        }

        var snapshot = new SnapshotBuilder(_dbContext, ownerUserId, rootRevision, cancellationToken);
        await snapshot.VisitAsync(agentflowId, rootRevision, new HashSet<Guid> { agentflowId }).ConfigureAwait(false);
        return snapshot.Build();
    }

    public async Task<bool> DefinitionsRemainAvailableAsync(
        Guid rootAgentflowId,
        AgentflowRevisionSnapshot snapshot,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var (agentflowId, revision) in snapshot.GetDefinitions(rootAgentflowId))
        {
            if (!await RevisionExistsAsync(agentflowId, revision, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (!await NodesRemainAvailableAsync(agentflowId, revision, rootAgentflowId, snapshot, cancellationToken))
            {
                return false;
            }

            if (!await MembersRemainAvailableAsync(agentflowId, revision, rootAgentflowId, snapshot, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private Task<bool> RevisionExistsAsync(Guid agentflowId, int revision, CancellationToken cancellationToken) =>
        _dbContext.AgentflowRevisions.AnyAsync(
            row => row.AgentflowId == agentflowId && row.Revision == revision,
            cancellationToken
        );

    private async Task<bool> NodesRemainAvailableAsync(
        Guid agentflowId,
        int revision,
        Guid rootAgentflowId,
        AgentflowRevisionSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var nodes = await _dbContext
            .AgentflowNodes.AsNoTracking()
            .Where(node => node.AgentflowId == agentflowId && node.AgentflowRevision == revision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var node in nodes)
        {
            if (node.Kind == AgentflowNodeKind.Agent && node.RelateId is { } agentId)
            {
                if (!await _dbContext.Agents.AnyAsync(agent => agent.Id == agentId, cancellationToken))
                {
                    return false;
                }
            }
            else if (
                node.Kind == AgentflowNodeKind.WorkflowAsAgent
                && node.RelateId is { } targetAgentflowId
                && !ContainsReference(
                    snapshot,
                    rootAgentflowId,
                    agentflowId,
                    revision,
                    node.NodeId,
                    null,
                    targetAgentflowId
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> MembersRemainAvailableAsync(
        Guid agentflowId,
        int revision,
        Guid rootAgentflowId,
        AgentflowRevisionSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var members = await _dbContext
            .AgentflowBlockMembers.AsNoTracking()
            .Where(member => member.AgentflowId == agentflowId && member.AgentflowRevision == revision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var member in members)
        {
            if (member.Kind == AgentflowNodeKind.Agent)
            {
                if (!await _dbContext.Agents.AnyAsync(agent => agent.Id == member.RelateId, cancellationToken))
                {
                    return false;
                }
            }
            else if (
                member.Kind == AgentflowNodeKind.WorkflowAsAgent
                && !ContainsReference(
                    snapshot,
                    rootAgentflowId,
                    agentflowId,
                    revision,
                    member.NodeId,
                    member.MemberId,
                    member.RelateId
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsReference(
        AgentflowRevisionSnapshot snapshot,
        Guid rootAgentflowId,
        Guid parentAgentflowId,
        int parentRevision,
        string parentNodeId,
        string? blockMemberId,
        Guid targetAgentflowId
    ) =>
        snapshot.Dependencies.Any(dependency =>
            dependency.ParentAgentflowId == parentAgentflowId
            && dependency.ParentRevision == parentRevision
            && dependency.ParentNodeId == parentNodeId
            && dependency.BlockMemberId == blockMemberId
            && dependency.TargetAgentflowId == targetAgentflowId
            && snapshot.PinsDefinition(rootAgentflowId, dependency.TargetAgentflowId, dependency.TargetRevision)
        );

    private sealed class SnapshotBuilder
    {
        private readonly IAgentsDbContext _dbContext;
        private readonly string _ownerUserId;
        private readonly int _rootRevision;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<Guid, int> _publishedRevisions = [];
        private readonly HashSet<(Guid AgentflowId, int Revision)> _expandedDefinitions = [];
        private readonly List<AgentflowDependencyRevision> _dependencies = [];

        public SnapshotBuilder(
            IAgentsDbContext dbContext,
            string ownerUserId,
            int rootRevision,
            CancellationToken cancellationToken
        )
        {
            _dbContext = dbContext;
            _ownerUserId = ownerUserId;
            _rootRevision = rootRevision;
            _cancellationToken = cancellationToken;
        }

        public async Task VisitAsync(Guid agentflowId, int revision, IReadOnlySet<Guid> path)
        {
            if (!_expandedDefinitions.Add((agentflowId, revision)))
            {
                return;
            }

            var nodeReferences = await _dbContext
                .AgentflowNodes.AsNoTracking()
                .Where(node =>
                    node.AgentflowId == agentflowId
                    && node.AgentflowRevision == revision
                    && node.Kind == AgentflowNodeKind.WorkflowAsAgent
                    && node.RelateId.HasValue
                )
                .Select(node => new { node.NodeId, TargetId = node.RelateId!.Value })
                .ToListAsync(_cancellationToken)
                .ConfigureAwait(false);
            var memberReferences = await _dbContext
                .AgentflowBlockMembers.AsNoTracking()
                .Where(member =>
                    member.AgentflowId == agentflowId
                    && member.AgentflowRevision == revision
                    && member.Kind == AgentflowNodeKind.WorkflowAsAgent
                )
                .Select(member => new
                {
                    member.NodeId,
                    member.MemberId,
                    TargetId = member.RelateId,
                })
                .ToListAsync(_cancellationToken)
                .ConfigureAwait(false);

            foreach (var reference in nodeReferences)
            {
                await VisitReferenceAsync(agentflowId, revision, reference.NodeId, null, reference.TargetId, path)
                    .ConfigureAwait(false);
            }

            foreach (var reference in memberReferences)
            {
                await VisitReferenceAsync(
                        agentflowId,
                        revision,
                        reference.NodeId,
                        reference.MemberId,
                        reference.TargetId,
                        path
                    )
                    .ConfigureAwait(false);
            }
        }

        public AgentflowRevisionSnapshot Build() => new(_rootRevision, _dependencies);

        private async Task VisitReferenceAsync(
            Guid parentAgentflowId,
            int parentRevision,
            string parentNodeId,
            string? blockMemberId,
            Guid targetAgentflowId,
            IReadOnlySet<Guid> path
        )
        {
            if (path.Contains(targetAgentflowId))
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"Nested Agentflow cycle at '{targetAgentflowId}'.");
            }

            var targetRevision = await GetPublishedRevisionAsync(targetAgentflowId).ConfigureAwait(false);
            if (
                !await _dbContext
                    .AgentflowRevisions.AnyAsync(
                        row => row.AgentflowId == targetAgentflowId && row.Revision == targetRevision,
                        _cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                throw new AgwException(ErrorCodes.AgentflowRevisionNotFound);
            }

            _dependencies.Add(
                new AgentflowDependencyRevision(
                    parentAgentflowId,
                    parentRevision,
                    parentNodeId,
                    blockMemberId,
                    targetAgentflowId,
                    targetRevision
                )
            );

            if (_expandedDefinitions.Contains((targetAgentflowId, targetRevision)))
            {
                return;
            }

            var nextPath = path.Append(targetAgentflowId).ToHashSet();
            await VisitAsync(targetAgentflowId, targetRevision, nextPath).ConfigureAwait(false);
        }

        private async Task<int> GetPublishedRevisionAsync(Guid agentflowId)
        {
            if (_publishedRevisions.TryGetValue(agentflowId, out var revision))
            {
                return revision;
            }

            var target = await _dbContext
                .Agentflows.AsNoTracking()
                .FirstOrDefaultAsync(
                    agentflow => agentflow.Id == agentflowId && agentflow.CreateBy == _ownerUserId,
                    _cancellationToken
                )
                .ConfigureAwait(false);
            if (target is null)
            {
                throw new AgwException(ErrorCodes.ResourceNotFound);
            }

            if (target.PublishedRevision == 0)
            {
                throw new AgwException(ErrorCodes.AgentflowNotPublished);
            }

            _publishedRevisions.Add(agentflowId, target.PublishedRevision);
            return target.PublishedRevision;
        }
    }
}
