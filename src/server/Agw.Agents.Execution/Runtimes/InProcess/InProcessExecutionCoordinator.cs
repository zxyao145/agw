using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Files.Abstracts;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Execution.Runtimes.InProcess;

/// <summary>
/// 在连接所在进程的后台任务中执行 Turn：判定 Runtime 能否复用，登记活动 Turn，并把消息写到本 Turn 的广播。
/// Runs turns in background tasks of the connection's process: decides whether the Runtime can be reused, registers the active turn and writes messages to the turn's broadcast.
/// </summary>
internal sealed class InProcessExecutionCoordinator : IExecutionCoordinator, IAsyncDisposable
{
    private readonly IAgentRuntimeFactory _agentRuntimes;
    private readonly AgentTurnExecutor _agentTurns;
    private readonly AgentflowTurnExecutor _agentflowTurns;
    private readonly ExecutionContextFactory _executionContexts;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly IConversationExecutionGate? _conversationGate;
    private readonly TurnBroadcastRegistry _broadcasts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationToken _hostToken;
    private readonly Action<int>? _pendingInteractionCountChanged;

    public InProcessExecutionCoordinator(
        IAgentRuntimeFactory agentRuntimes,
        AgentTurnExecutor agentTurns,
        AgentflowTurnExecutor agentflowTurns,
        ExecutionContextFactory executionContexts,
        IAgwFileSystemResolver fileSystemResolver,
        IConversationExecutionGate? conversationGate,
        TurnBroadcastRegistry broadcasts,
        IServiceScopeFactory scopeFactory,
        CancellationToken hostToken,
        Action<int>? pendingInteractionCountChanged = null
    )
    {
        _agentRuntimes = agentRuntimes;
        _agentTurns = agentTurns;
        _agentflowTurns = agentflowTurns;
        _executionContexts = executionContexts;
        _fileSystemResolver = fileSystemResolver;
        _conversationGate = conversationGate;
        _broadcasts = broadcasts;
        _scopeFactory = scopeFactory;
        _hostToken = hostToken;
        _pendingInteractionCountChanged = pendingInteractionCountChanged;
    }

    /// <summary>
    /// 当前保留的 Runtime 与其活动 Turn；供连接处理中断、回答、模式与 Checkpoint 命令。
    /// The retained Runtime and its active turn, used by the connection for interrupt, answer, mode and checkpoint commands.
    /// </summary>
    internal InProcessTurnHost? Host { get; private set; }

    public async Task<ExecutionReceipt> StartAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var context = await _executionContexts.CreateAsync(
            new ExecutionContextRequest(
                request.UserId,
                request.TurnId,
                request.Target.AgentType,
                request.Target.AgentId,
                request.Task,
                request.WorkspaceSnapshot,
                request.Settings.PermissionMode,
                request.Settings.PermissionVersion,
                ExecutionProvider.InProcess
            ),
            cancellationToken
        );
        var output =
            _broadcasts.Find(request.TurnId, request.UserId)
            ?? throw new AgwException(ErrorCodes.AgentExecutionFailed, "The accepted turn has no output.");
        var scope = ExecutionScope.Create(
            context,
            request.Task.TaskId,
            output,
            new InMemoryPendingInteractionSet(
                context.PermissionMode,
                requestOutput: output,
                pendingCountChanged: _pendingInteractionCountChanged
            ),
            new InMemoryTurnCheckpointStore(),
            new TurnRecord(_scopeFactory, request.TurnId, request.InputMessageId, writeGuard: null)
        );
        using var executionScope = scope.Push();
        ProjectWorkspacePaths.EnsureAvailable(request.Task.ProjectId, request.WorkspaceSnapshot);
        if (Host != null && !await CanReuseAsync(Host, request, _hostToken))
        {
            await ReleaseAsync();
        }

        var lease =
            _conversationGate == null
                ? null
                : await _conversationGate.AcquireAsync(
                    request.Task.ProjectConversationId,
                    request.Task.Generation,
                    _hostToken
                );
        var ownership = CancellationTokenSource.CreateLinkedTokenSource(
            _hostToken,
            lease?.HandleLostToken ?? CancellationToken.None
        );
        try
        {
            var activeTurn = await StartTurnAsync(scope, request, ownership.Token);
            if (activeTurn == null)
            {
                if (lease != null)
                    await lease.DisposeAsync();
                ownership.Dispose();
            }
            else
            {
                _ = ReleaseExecutionLeaseAsync(Host!.WhenIdleAsync(), lease, ownership);
            }
            return new ExecutionReceipt(request.TurnId, activeTurn != null);
        }
        catch
        {
            if (lease != null)
                await lease.DisposeAsync();
            ownership.Dispose();
            throw;
        }
    }

    public Task SetModeAsync(string mode, CancellationToken cancellationToken)
    {
        var runtime =
            Host?.AgentRuntime
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                "The current execution target does not support mode changes."
            );
        return _agentRuntimes.SetModeAsync(runtime, mode, cancellationToken);
    }

    /// <summary>
    /// 释放保留的 Runtime；活动 Turn 先被中断并等待结束。
    /// Releases the retained Runtime; an active turn is interrupted and awaited first.
    /// </summary>
    public async Task ReleaseAsync()
    {
        var host = Host;
        Host = null;
        if (host != null)
        {
            await host.DisposeAsync();
        }
        _pendingInteractionCountChanged?.Invoke(0);
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();

    private async Task<ActiveTurn?> StartTurnAsync(
        ExecutionScope scope,
        ExecutionRequest request,
        CancellationToken cancellationToken
    )
    {
        await EnsureWorkspaceAsync(scope, cancellationToken);
        var unattended = request.Settings.HumanInteractionPolicy == HumanInteractionPolicy.Reject;
        var host = Host;
        if (host == null)
        {
            host = await CreateHostAsync(request, unattended, cancellationToken);
            if (host == null)
            {
                return null;
            }
            Host = host;
        }

        await scope.Turn!.MarkRunningAsync(cancellationToken);
        if (host.AgentRuntime is { } agent)
        {
            if (!string.IsNullOrWhiteSpace(request.RequestedMode))
            {
                await _agentRuntimes.SetModeAsync(agent, request.RequestedMode, cancellationToken);
            }

            // ResultOnly 只在本轮会产生 result 消息时生效：过滤出站消息并拒绝人机交互。
            // ResultOnly applies only when the turn produces a result: outbound messages are filtered and interaction is declined.
            var resultOnly = request.Settings.ResultOnly && agent.EmitsTurnResult;
            var sink = ResultOnlyMessageSink.Wrap(scope.Output, resultOnly);
            if (!unattended && scope.Context.EngineKind == EngineKind.Maf)
            {
                BindApprovalBatches(scope, allowInteraction: !resultOnly);
                return host.StartTurn(
                    scope,
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
                    agent.CancelActiveRequest,
                    ct => ExecuteAgentTurnAsync(scope, agent, request, sink, ct),
                    async (response, ct) =>
                        await scope.Interactions.TryResolveAsync(response, ct).ConfigureAwait(false) != null
                );
            }

            var interactions = BindInteractions(scope, sink, unattended, allowInteraction: !resultOnly);
            return host.StartTurn(
                scope,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
                () =>
                {
                    agent.CancelActiveRequest();
                    interactions?.CancelAll();
                },
                ct => ExecuteAgentTurnAsync(scope, agent, request, sink, ct),
                interactions == null ? null : interactions.TrySubmitAsync
            );
        }

        var agentflow = host.AgentflowRuntime!;
        var agentflowInteractions = BindInteractions(scope, scope.Output, unattended, allowInteraction: true);
        return host.StartTurn(
            scope,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
            () => agentflowInteractions?.CancelAll(),
            ct =>
                TurnPipeline.RunAsync(
                    request.Envelope,
                    scope,
                    _agentflowTurns.RunAsync(scope, agentflow, CreateAgentflowInput(request), ct),
                    request.Stream,
                    scope.Output,
                    ct
                ),
            agentflowInteractions == null ? null : agentflowInteractions.TrySubmitAsync
        );
    }

    /// <summary>
    /// System Agent 的交互式 Turn：Agent 管线交出的人工审批批次登记到本 Turn 的待处理集合，输入工具从集合读取已保存的回答。
    /// An interactive System Agent turn: human approval batches from the Agent pipeline are registered in this turn's pending set, and input tools read the saved answers from it.
    /// </summary>
    private static void BindApprovalBatches(ExecutionScope scope, bool allowInteraction)
    {
        var requests = new InteractionRequestRegistry();
        scope.BindInteractions(
            new PendingInteractionHandler(HumanInteractionPolicy.Allow, requests, allowInteraction),
            new ResolvedHumanInteractionChannel(scope.Interactions, allowInteraction),
            requests
        );
    }

    /// <summary>
    /// 为本 Turn 绑定决定来源：无人值守执行的人工请求直接失败；交互式执行通过即时通道在内存等待用户回答。
    /// Binds the decision source of this turn: human requests fail unattended execution; interactive execution waits in memory for user answers through the live channel.
    /// </summary>
    private InProcessInteractionSession? BindInteractions(
        ExecutionScope scope,
        IExecutionMessageSink sink,
        bool unattended,
        bool allowInteraction
    )
    {
        if (unattended)
        {
            scope.BindInteractions(new UnattendedInteractionHandler(), null, null);
            return null;
        }

        var interactions = new InProcessInteractionSession(
            sink,
            scope.Permissions,
            _pendingInteractionCountChanged,
            allowInteraction
        );
        scope.BindInteractions(interactions, interactions, interactions.Requests);
        return interactions;
    }

    private async Task<InProcessTurnHost?> CreateHostAsync(
        ExecutionRequest request,
        bool deferHumanInteractions,
        CancellationToken cancellationToken
    )
    {
        IAsyncDisposable? runtime = request.Target.AgentType switch
        {
            AgentRuntimeType.Agent => await _agentRuntimes.CreateRuntimeAsync(
                request.Target.AgentId,
                request.Task,
                request.Settings,
                cancellationToken
            ),
            AgentRuntimeType.Agentflow => AgentflowRuntimeFactory.CreateRuntime(
                request.Target.AgentId,
                request.Task,
                request.Settings,
                deferHumanInteractions
            ),
            _ => throw new AgwException(
                ErrorCodes.UnsupportedAgentType,
                $"Agent runtime type '{request.Target.AgentType}' is not supported."
            ),
        };
        return runtime == null
            ? null
            : new InProcessTurnHost(
                request.Target,
                runtime,
                request.WorkspaceSnapshot.Fingerprint,
                request.Settings.PermissionVersion
            );
    }

    private async Task ExecuteAgentTurnAsync(
        ExecutionScope scope,
        AgentRuntime runtime,
        ExecutionRequest request,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        runtime.ResetCancellationToken();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runtime.CancellationToken
        );
        var linkedToken = linkedCts.Token;
        await TurnPipeline.RunAsync(
            request.Envelope,
            scope,
            _agentTurns.RunAsync(scope, runtime, new TurnInput(request.Input), linkedToken),
            request.Stream,
            sink,
            linkedToken
        );
    }

    private static TurnInput CreateAgentflowInput(ExecutionRequest request) =>
        request.ResumeCheckpoint is not { } checkpoint
            ? new TurnInput(request.Input)
            : new TurnInput(request.Input)
            {
                Resume = new TurnResume
                {
                    Checkpoint = checkpoint.Checkpoint,
                    CheckpointNodeIds = checkpoint
                        .Markers.Select(marker => marker.NodeId)
                        .ToHashSet(StringComparer.Ordinal),
                },
            };

    /// <summary>
    /// 判断保留的 Runtime 能否执行本 Turn：目标、工作目录与定义版本都不变，Agent 还要求项目、归一化 context 与 Generation 相同。
    /// Decides whether the retained Runtime can run this turn: the target, workspace and definition version are unchanged, and an Agent also needs the same project, normalized context and generation.
    /// </summary>
    private async Task<bool> CanReuseAsync(
        InProcessTurnHost host,
        ExecutionRequest request,
        CancellationToken cancellationToken
    )
    {
        if (host.Target != request.Target || host.WorkspaceFingerprint != request.WorkspaceSnapshot.Fingerprint)
        {
            return false;
        }
        if (host.AgentRuntime is not { } runtime)
        {
            return true;
        }

        var contextId = ContextIdUtil.ResolveContextId(
            string.IsNullOrWhiteSpace(request.Settings.ContextId) ? request.Task.ContextId : request.Settings.ContextId
        );
        return string.Equals(runtime._contextId, contextId, StringComparison.Ordinal)
            && runtime._projectId == request.Task.ProjectId
            && (runtime.SessionStateScope?.Generation ?? 0) == request.Task.Generation
            && runtime.SessionStateScope?.AgentId == request.Target.AgentId
            && (runtime.AgentType != AgentType.External || host.PermissionVersion == request.Settings.PermissionVersion)
            && await _agentRuntimes.IsRuntimeCurrentAsync(runtime, cancellationToken);
    }

    private async Task EnsureWorkspaceAsync(ExecutionScope scope, CancellationToken cancellationToken)
    {
        var fs =
            await _fileSystemResolver.ResolveSnapshotAsync(
                scope.ProjectId,
                scope.WorkspaceSnapshot,
                null,
                cancellationToken
            ) ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Project was not found.");
        if (await fs.StatAsync("", cancellationToken) == null)
        {
            await fs.CreateDirectoryAsync("", cancellationToken);
        }
    }

    private static async Task ReleaseExecutionLeaseAsync(
        Task completion,
        IApplicationLockLease? lease,
        CancellationTokenSource ownership
    )
    {
        try
        {
            await completion;
        }
        catch (Exception)
        { /* TurnPipeline owns execution error reporting. */
        }
        finally
        {
            if (lease != null)
                await lease.DisposeAsync();
            ownership.Dispose();
        }
    }
}
