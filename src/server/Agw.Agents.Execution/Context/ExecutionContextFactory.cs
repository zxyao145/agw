using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Execution.Context;

/// <summary>
/// 在服务端完成所属与权限校验后构造一次 Turn 的执行数据。
/// Builds the execution data of one turn after the server has verified ownership and permissions.
/// </summary>
public sealed class ExecutionContextFactory
{
    private readonly IAgentsDbContext _db;

    public ExecutionContextFactory(IAgentsDbContext db)
    {
        _db = db;
    }

    public async Task<AgentExecutionContext> CreateAsync(
        ExecutionContextRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        Guid? agentId = null;
        EngineKind? engineKind = null;
        if (request.RuntimeType == AgentRuntimeType.Agent)
        {
            agentId = request.TargetId;
            engineKind = await ResolveEngineKindAsync(request.TargetId, cancellationToken).ConfigureAwait(false);
        }

        return new AgentExecutionContext
        {
            UserId = request.UserId,
            ProjectId = request.Task.ProjectId,
            ProjectConversationId = request.Task.ProjectConversationId,
            ContextId = request.Task.ContextId,
            Generation = request.Task.Generation,
            WorkspaceSnapshot = request.WorkspaceSnapshot,
            TurnId = request.TurnId,
            TurnTargetId = request.TargetId,
            RuntimeType = request.RuntimeType,
            AgentId = agentId,
            EngineKind = engineKind,
            PermissionMode = request.PermissionMode,
            PermissionVersion = request.PermissionVersion,
            Provider = request.Provider,
        };
    }

    /// <summary>
    /// 读取当前用户拥有的 Agent Definition 的 Engine 种类；不存在或不属于当前用户时报告 AgentNotFound。
    /// Reads the Engine kind of an Agent definition owned by the current user; reports AgentNotFound otherwise.
    /// </summary>
    private async Task<EngineKind> ResolveEngineKindAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var owner = UserInfoUtil.RequiredUserId;
        var agent =
            await _db
                .Agents.AsNoTracking()
                .Where(item => item.Id == agentId && item.CreateBy == owner)
                .Select(item => new { item.Type, item.ExternalAgentKind })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.AgentNotFound);
        return EngineKinds.Resolve(agent.Type, agent.ExternalAgentKind);
    }
}

public sealed record ExecutionContextRequest(
    string UserId,
    Guid TurnId,
    AgentRuntimeType RuntimeType,
    Guid TargetId,
    AgentExecutionTask Task,
    ProjectWorkspaceSnapshot WorkspaceSnapshot,
    AgwPermissionMode? PermissionMode,
    long PermissionVersion,
    ExecutionProvider Provider
);
