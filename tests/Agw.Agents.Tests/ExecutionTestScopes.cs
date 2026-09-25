using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Runtime;

namespace Agw.Agents.Tests;

/// <summary>
/// 构造测试使用的真实执行数据与执行作用域。
/// Builds real execution data and execution scopes for tests.
/// </summary>
internal static class ExecutionTestScopes
{
    public static AgentExecutionContext Context(
        Guid? projectId = null,
        string contextId = "context",
        string userId = "user-1",
        int generation = 0,
        Guid? agentId = null,
        AgentRuntimeType runtimeType = AgentRuntimeType.Agent,
        EngineKind? engineKind = EngineKind.Maf,
        AgwPermissionMode? permissionMode = null,
        long permissionVersion = 0,
        ExecutionProvider provider = ExecutionProvider.InProcess,
        Guid? conversationId = null,
        string workspace = "/workspace"
    )
    {
        var resolvedProjectId = projectId ?? Guid.CreateVersion7();
        var resolvedAgentId = agentId ?? Guid.CreateVersion7();
        return new AgentExecutionContext
        {
            UserId = userId,
            ProjectId = resolvedProjectId,
            ProjectConversationId = conversationId ?? Guid.CreateVersion7(),
            ContextId = contextId,
            Generation = generation,
            WorkspaceSnapshot = new ProjectWorkspaceSnapshot(workspace, [], "fingerprint"),
            TurnId = Guid.CreateVersion7(),
            TurnTargetId = resolvedAgentId,
            RuntimeType = runtimeType,
            AgentId = runtimeType == AgentRuntimeType.Agent ? resolvedAgentId : null,
            EngineKind = runtimeType == AgentRuntimeType.Agent ? engineKind : null,
            PermissionMode = permissionMode,
            PermissionVersion = permissionVersion,
            Provider = provider,
        };
    }

    public static ExecutionScope Scope(
        AgentExecutionContext? context = null,
        IExecutionMessageSink? output = null,
        Guid? taskId = null
    )
    {
        context ??= Context();
        return ExecutionScope.Create(
            context,
            taskId ?? Guid.CreateVersion7(),
            output ?? new InteractionTestSink(),
            new InMemoryPendingInteractionSet(context.PermissionMode)
        );
    }

    /// <summary>
    /// 按清单构造一个 Durable 分段的执行作用域，与分段执行器的构造方式一致。
    /// Builds the execution scope of one durable segment from its manifest, as the segment executor does.
    /// </summary>
    public static ExecutionScope SegmentScope(
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink
    )
    {
        var context = new AgentExecutionContext
        {
            UserId = manifest.UserId,
            ProjectId = manifest.Task.ProjectId,
            ProjectConversationId = manifest.Task.ProjectConversationId,
            ContextId = manifest.Task.ContextId,
            Generation = manifest.Task.Generation,
            WorkspaceSnapshot =
                manifest.WorkspaceSnapshot ?? new ProjectWorkspaceSnapshot("/workspace", [], "fingerprint"),
            TurnId = manifest.ExecutionId,
            TurnTargetId = manifest.AgentId,
            RuntimeType = manifest.AgentType,
            AgentId = manifest.AgentType == AgentRuntimeType.Agent ? manifest.AgentId : null,
            EngineKind = manifest.AgentType == AgentRuntimeType.Agent ? EngineKind.Maf : null,
            PermissionMode = manifest.Settings.PermissionMode,
            PermissionVersion = manifest.Settings.PermissionVersion,
            Provider = ExecutionProvider.Distributed,
        };
        return ExecutionScope.Create(
            context,
            manifest.Task.TaskId,
            sink,
            new DurablePendingInteractionSet(
                context.PermissionMode,
                DurableExecutionCoordinator.RestoreInteractions(input)
            )
        );
    }

    /// <summary>
    /// 建立属于指定对话与 Generation 的执行作用域；用户沿用当前身份。
    /// Pushes an execution scope bound to the given conversation and generation, keeping the current user.
    /// </summary>
    public static IDisposable PushIdentity(Guid projectId, string contextId, int generation) =>
        Scope(
                Context(
                    projectId: projectId,
                    contextId: contextId,
                    userId: UserInfoUtil.UserId ?? "user-1",
                    generation: generation
                )
            )
            .Push();

    /// <summary>
    /// 建立绑定了回答通道与请求登记服务的执行作用域；用户沿用当前身份。
    /// Pushes an execution scope bound to the answer channel and request registry, keeping the current user.
    /// </summary>
    public static IDisposable PushInteractions(
        IHumanInteractionChannel? channel,
        IInteractionRequestRegistry? requests = null,
        InteractionPermissionState? permissions = null,
        IInteractionHandler? handler = null
    )
    {
        var scope = Scope(
            Context(
                userId: UserInfoUtil.UserId ?? "user-1",
                permissionMode: permissions?.Current,
                permissionVersion: permissions?.Snapshot.Version ?? 0
            )
        );
        scope.BindInteractions(handler, channel, requests);
        return scope.Push();
    }

    /// <summary>
    /// 构造读取已保存输入回答的回答通道。
    /// Builds an answer channel that reads saved input answers.
    /// </summary>
    public static ResolvedHumanInteractionChannel ResolvedChannel(
        IReadOnlyList<DurableResolvedInteraction> resolvedInputs,
        bool allowInteraction = true
    ) =>
        new(
            new InMemoryPendingInteractionSet(
                null,
                DurableExecutionCoordinator.RestoreInteractions(
                    new DurableExecutionSegmentInput(Guid.CreateVersion7(), 0, [], null)
                    {
                        ResolvedInputs = resolvedInputs,
                    }
                )
            ),
            allowInteraction
        );

    /// <summary>
    /// 按清单建立 Durable 分段的执行作用域，再用协调器的分段入口执行一个 Agentflow 分段。
    /// Establishes the durable segment scope from the manifest and runs one Agentflow segment through the coordinator's segment entry.
    /// </summary>
    public static async Task<DurableExecutionSegmentResult> ExecuteDurableSegmentInScopeAsync(
        this AgentflowTurnExecutor executor,
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        var scope = SegmentScope(manifest, input, sink);
        using var pushed = scope.Push();
        return await DurableExecutionCoordinator.RunAgentflowSegmentAsync(
            scope,
            manifest,
            input,
            sink,
            executor,
            cancellationToken
        );
    }

    /// <summary>
    /// 按清单建立 Durable 分段的执行作用域，再用协调器的分段入口执行一个 Agent 分段。
    /// Establishes the durable segment scope from the manifest and runs one Agent segment through the coordinator's segment entry.
    /// </summary>
    public static async Task<DurableExecutionSegmentResult> ExecuteDurableSegmentInScopeAsync(
        this AgentTurnExecutor executor,
        IAgentRuntimeFactory agentRuntimes,
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        var scope = SegmentScope(manifest, input, sink);
        using var pushed = scope.Push();
        return await DurableExecutionCoordinator.RunAgentSegmentAsync(
            scope,
            manifest,
            input,
            sink,
            agentRuntimes,
            executor,
            cancellationToken
        );
    }
}
