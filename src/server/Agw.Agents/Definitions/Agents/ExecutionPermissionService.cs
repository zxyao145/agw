using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public sealed class ExecutionPermissionService
{
    private readonly IAgentsDbContext _db;
    private readonly IAgentflowRevisionSnapshotReader _snapshots;

    public ExecutionPermissionService(IAgentsDbContext db, IAgentflowRevisionSnapshotReader snapshots)
    {
        _db = db;
        _snapshots = snapshots;
    }

    public static ExecutionPermissionCapabilities ForAgent(Agent agent) =>
        agent switch
        {
            var definition when ExternalAgentRules.UsesA2AEndpoint(definition) => new(
                [AgwPermissionMode.FullAccess],
                SupportsNoTools: false,
                "Remote A2A agents run their own tools; Agw permission modes do not apply."
            ),
            { Type: AgentType.External, ExternalAgentKind: EngineKind.Codex or EngineKind.Pi } => new(
                [AgwPermissionMode.FullAccess],
                SupportsNoTools: false,
                $"The current {agent.ExternalAgentKind} SDK integration supports Full access only."
            ),
            _ => new(
                [AgwPermissionMode.FullAccess, AgwPermissionMode.AlwaysAsk, AgwPermissionMode.AllowSameArguments],
                SupportsNoTools: agent.Type == AgentType.System || agent.ExternalAgentKind == EngineKind.ClaudeCode
            ),
        };

    public static void Validate(Agent agent, AgwPermissionMode? mode) => Validate(ForAgent(agent), mode);

    public static void Validate(ExecutionPermissionCapabilities capabilities, AgwPermissionMode? mode)
    {
        if (mode.HasValue && !capabilities.SupportedPermissionModes.Contains(mode.Value))
            throw new AgwException(
                ErrorCodes.InvalidParam,
                capabilities.Reason ?? "This permission mode is not supported."
            );
    }

    public Task<ExecutionPermissionCapabilities> GetAsync(
        AgentRuntimeType type,
        Guid id,
        CancellationToken ct = default
    ) => GetAsync(type, id, agentflowRevision: null, ct: ct);

    public async Task<ExecutionPermissionCapabilities> GetAsync(
        AgentRuntimeType type,
        Guid id,
        int? agentflowRevision,
        CancellationToken ct = default
    )
    {
        var owner = UserInfoUtil.RequiredUserId;
        if (agentflowRevision is <= 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        if (type == AgentRuntimeType.Agent)
        {
            if (agentflowRevision.HasValue)
            {
                throw new AgwException(ErrorCodes.InvalidParam);
            }

            var agent =
                await _db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.CreateBy == owner, ct)
                ?? throw new AgwException(ErrorCodes.ResourceNotFound);
            return ForAgent(agent);
        }
        if (type != AgentRuntimeType.Agentflow)
            throw new AgwException(ErrorCodes.InvalidParam);
        var snapshot = await _snapshots.CreateRevisionSnapshotAsync(id, agentflowRevision ?? 0, ct);
        return await GetAgentflowAsync(id, snapshot, ct);
    }

    public Task<ExecutionPermissionCapabilities> GetForTargetAsync(
        AgentRuntimeType type,
        Guid id,
        AgentflowRevisionSnapshot? snapshot,
        CancellationToken ct = default
    )
    {
        if (snapshot is null)
        {
            return GetAsync(type, id, ct);
        }

        if (type != AgentRuntimeType.Agentflow)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        return GetAgentflowAsync(id, snapshot, ct);
    }

    public async Task<ExecutionPermissionCapabilities> GetAgentflowAsync(
        Guid agentflowId,
        AgentflowRevisionSnapshot snapshot,
        CancellationToken ct = default
    )
    {
        if (snapshot.RootRevision <= 0)
            throw new AgwException(ErrorCodes.AgentflowRevisionNotFound);

        var agentIds = await GetAgentIdsAsync(agentflowId, snapshot, ct).ConfigureAwait(false);
        var agents = await _db.Agents.AsNoTracking().Where(agent => agentIds.Contains(agent.Id)).ToListAsync(ct);
        if (agents.Count != agentIds.Count)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        return IntersectCapabilities(agents);
    }

    private async Task<HashSet<Guid>> GetAgentIdsAsync(
        Guid agentflowId,
        AgentflowRevisionSnapshot snapshot,
        CancellationToken ct
    )
    {
        var definitions = snapshot
            .Dependencies.Select(dependency => (Id: dependency.TargetAgentflowId, Revision: dependency.TargetRevision))
            .Prepend((agentflowId, snapshot.RootRevision))
            .Distinct();
        var agentIds = new HashSet<Guid>();
        foreach (var (id, revision) in definitions)
        {
            await AddDefinitionAgentIdsAsync(agentIds, id, revision, ct).ConfigureAwait(false);
        }

        return agentIds;
    }

    private async Task AddDefinitionAgentIdsAsync(
        HashSet<Guid> agentIds,
        Guid agentflowId,
        int revision,
        CancellationToken ct
    )
    {
        var nodes = _db
            .AgentflowNodes.Where(node =>
                node.AgentflowId == agentflowId
                && node.AgentflowRevision == revision
                && node.Kind == AgentflowNodeKind.Agent
                && node.RelateId.HasValue
            )
            .Select(node => node.RelateId!.Value);
        var members = _db
            .AgentflowBlockMembers.Where(member =>
                member.AgentflowId == agentflowId
                && member.AgentflowRevision == revision
                && member.Kind == AgentflowNodeKind.Agent
            )
            .Select(member => member.RelateId);
        agentIds.UnionWith(await nodes.Union(members).ToListAsync(ct).ConfigureAwait(false));
    }

    private static ExecutionPermissionCapabilities IntersectCapabilities(IReadOnlyList<Agent> agents)
    {
        var supported = new HashSet<AgwPermissionMode>(Enum.GetValues<AgwPermissionMode>());
        var supportsNoTools = true;
        string? reason = null;
        foreach (var agent in agents)
        {
            var child = ForAgent(agent);
            supported.IntersectWith(child.SupportedPermissionModes);
            supportsNoTools &= child.SupportsNoTools;
            reason ??= child.Reason;
        }
        return new(supported.Order().ToArray(), supportsNoTools, reason);
    }
}
