using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Contracts;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public sealed class ExecutionPermissionService
{
    private readonly IAgentsDbContext _db;

    public ExecutionPermissionService(IAgentsDbContext db)
    {
        _db = db;
    }

    public static ExecutionPermissionCapabilities ForAgent(Agent agent) =>
        agent.Type == AgentType.External && agent.ExternalAgentKind is ExternalAgentKind.Codex or ExternalAgentKind.Pi
            ? new(
                [AgwPermissionMode.FullAccess],
                $"The current {agent.ExternalAgentKind} SDK integration supports Full access only."
            )
            : new([AgwPermissionMode.FullAccess, AgwPermissionMode.AlwaysAsk, AgwPermissionMode.AllowSameArguments]);

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
    ) => GetCoreAsync(type, id, new HashSet<Guid>(), ct);

    private async Task<ExecutionPermissionCapabilities> GetCoreAsync(
        AgentRuntimeType type,
        Guid id,
        HashSet<Guid> visited,
        CancellationToken ct
    )
    {
        var owner = UserInfoUtil.RequiredUserId;
        if (type == AgentRuntimeType.Agent)
        {
            var agent =
                await _db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.CreateBy == owner, ct)
                ?? throw new AgwException(ErrorCodes.ResourceNotFound);
            return ForAgent(agent);
        }
        if (type != AgentRuntimeType.Agentflow)
            throw new AgwException(ErrorCodes.InvalidParam);
        var flow =
            await _db
                .Agentflows.AsNoTracking()
                .Include(a => a.Nodes)
                .SingleOrDefaultAsync(a => a.Id == id && a.CreateBy == owner, ct)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        if (!visited.Add(id))
            throw new AgwException(ErrorCodes.InvalidParam, "Recursive Agentflow references are not supported.");
        var supported = new HashSet<AgwPermissionMode>(Enum.GetValues<AgwPermissionMode>());
        string? reason = null;
        foreach (
            var node in flow.Nodes.Where(n =>
                n.RelateId.HasValue && n.Kind is AgentflowNodeKind.Agent or AgentflowNodeKind.WorkflowAsAgent
            )
        )
        {
            var child = await GetCoreAsync(
                node.Kind == AgentflowNodeKind.Agent ? AgentRuntimeType.Agent : AgentRuntimeType.Agentflow,
                node.RelateId!.Value,
                visited,
                ct
            );
            supported.IntersectWith(child.SupportedPermissionModes);
            reason ??= child.Reason;
        }
        visited.Remove(id);
        return new(supported.Order().ToArray(), reason);
    }
}
