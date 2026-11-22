using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Inbound.SignalR;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Executions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Execution.Agentflows.Checkpoints;

public sealed class AgentflowRevisionUsageReader : IAgentflowRevisionUsageReader
{
    private readonly IAgentsDbContext _dbContext;
    private readonly IConversationTurnStore _turns;
    private readonly IAgentflowRevisionSnapshotReader _snapshots;
    private readonly ExecutionConnectionRegistry? _connections;

    public AgentflowRevisionUsageReader(
        IAgentsDbContext dbContext,
        IConversationTurnStore turns,
        IAgentflowRevisionSnapshotReader snapshots,
        ExecutionConnectionRegistry? connections = null
    )
    {
        _dbContext = dbContext;
        _turns = turns;
        _snapshots = snapshots;
        _connections = connections;
    }

    public async Task<IReadOnlySet<(Guid AgentflowId, int Revision)>> ListPinnedRevisionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var revisions = new HashSet<(Guid AgentflowId, int Revision)>();
        await AddActiveExecutionRevisionsAsync(revisions, cancellationToken).ConfigureAwait(false);
        await AddAvailableCheckpointRevisionsAsync(revisions, cancellationToken).ConfigureAwait(false);
        return revisions;
    }

    private async Task AddActiveExecutionRevisionsAsync(
        HashSet<(Guid AgentflowId, int Revision)> revisions,
        CancellationToken cancellationToken
    )
    {
        // 读取完整实体，确保加密字段通过实体化拦截器解密。
        // Materialize full entities so encrypted fields pass through the materialization interceptor.
        var executions = await _dbContext
            .DurableExecutions.AsNoTracking()
            .Where(execution =>
                execution.Status != DurableExecutionStatus.Completed
                && execution.Status != DurableExecutionStatus.Failed
                && execution.Status != DurableExecutionStatus.Interrupted
            )
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var execution in executions)
        {
            var manifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
                execution.ManifestJson,
                "durable execution manifest"
            );
            if (manifest.AgentType != AgentRuntimeType.Agentflow)
            {
                continue;
            }

            AddSnapshot(revisions, manifest.AgentId, manifest.AgentflowRevision, manifest.AgentflowDependencies);
        }
    }

    private async Task AddAvailableCheckpointRevisionsAsync(
        HashSet<(Guid AgentflowId, int Revision)> revisions,
        CancellationToken cancellationToken
    )
    {
        var checkpoints = await _dbContext
            .AgentflowCheckpoints.AsNoTracking()
            .Where(checkpoint =>
                checkpoint.RuntimeVersion == AgentflowCheckpointRuntime.Version && checkpoint.SourceTurnId.HasValue
            )
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var inProcessOccurrences = _connections?.GetInProcessCheckpointOccurrenceIds() ?? new HashSet<Guid>();
        foreach (var checkpoint in checkpoints)
        {
            await AddCheckpointRevisionAsync(revisions, checkpoint, inProcessOccurrences, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task AddCheckpointRevisionAsync(
        HashSet<(Guid AgentflowId, int Revision)> revisions,
        AgentflowCheckpointRecord checkpoint,
        IReadOnlySet<Guid> inProcessOccurrences,
        CancellationToken cancellationToken
    )
    {
        if (!checkpoint.IsDurable && !inProcessOccurrences.Contains(checkpoint.Id))
        {
            return;
        }

        var turn = await _turns.GetAsync(checkpoint.SourceTurnId!.Value, cancellationToken).ConfigureAwait(false);
        if (turn is null)
        {
            return;
        }
        if (turn.TargetType != ConversationTurnTargetType.Agentflow || turn.AgentflowDependencies is null)
            return;

        var snapshot = AgentflowRevisionSnapshot.FromTurn(turn);
        if (!snapshot.PinsDefinition(turn.TargetId, checkpoint.AgentflowId, checkpoint.AgentflowRevision))
        {
            return;
        }

        var durableSnapshotMatches =
            !checkpoint.IsDurable
            || await DurableSnapshotMatchesAsync(checkpoint, snapshot, cancellationToken).ConfigureAwait(false);
        if (!durableSnapshotMatches)
        {
            return;
        }

        var definitionsRemainAvailable = await _snapshots
            .DefinitionsRemainAvailableAsync(turn.TargetId, snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (!definitionsRemainAvailable)
        {
            return;
        }

        AddSnapshot(revisions, turn.TargetId, snapshot.RootRevision, snapshot.Dependencies);
    }

    private async Task<bool> DurableSnapshotMatchesAsync(
        AgentflowCheckpointRecord checkpoint,
        AgentflowRevisionSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        if (!checkpoint.SourceTurnId.HasValue)
        {
            return false;
        }

        var execution = await _dbContext
            .DurableExecutions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == checkpoint.SourceTurnId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (execution is null)
        {
            return false;
        }

        var manifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
            execution.ManifestJson,
            "durable execution manifest"
        );
        return manifest.AgentType == AgentRuntimeType.Agentflow
            && manifest.AgentflowRevision == snapshot.RootRevision
            && snapshot.MatchesDependencies(manifest.AgentflowDependencies);
    }

    private static void AddSnapshot(
        HashSet<(Guid AgentflowId, int Revision)> revisions,
        Guid agentflowId,
        int rootRevision,
        IReadOnlyList<AgentflowDependencyRevision>? dependencies
    )
    {
        revisions.Add((agentflowId, rootRevision));
        if (dependencies is null)
        {
            return;
        }

        foreach (var dependency in dependencies)
        {
            revisions.Add((dependency.TargetAgentflowId, dependency.TargetRevision));
        }
    }
}
