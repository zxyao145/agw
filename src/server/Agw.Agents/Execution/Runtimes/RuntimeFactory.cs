using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Files.Abstracts;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Runtimes;

public readonly record struct RuntimeStartResult(RuntimeBase? Runtime, ActiveTurn? ActiveTurn);

public sealed record RuntimeStartRequest(
    Guid AgentId,
    AgentExecutionTask Task,
    ExecCommand Command,
    RuntimeBase? CurrentRuntime,
    RuntimeTurnContext TurnContext
)
{
    public string? RequestedMode { get; init; }
}

public interface IRuntimeFactory
{
    Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken);

    Task SetModeAsync(RuntimeBase runtime, string mode, CancellationToken cancellationToken) =>
        Task.FromException(
            new AgwException(ErrorCodes.InvalidParam, "The runtime factory does not support mode changes.")
        );

    Task SetPermissionModeAsync(
        RuntimeBase runtime,
        PermissionMode permissionMode,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;
}

public sealed class RuntimeFactory : IRuntimeFactory
{
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly IConversationExecutionGate? _conversationGate;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly RuntimeTurnContextAccessor _turnContextAccessor;
    private readonly HumanInteractionContextAccessor _humanInteractionContextAccessor;

    public RuntimeFactory(
        IAgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService,
        IAgwFileSystemResolver fileSystemResolver,
        RuntimeTurnContextAccessor turnContextAccessor,
        HumanInteractionContextAccessor humanInteractionContextAccessor,
        IConversationExecutionGate? conversationGate = null
    )
    {
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _fileSystemResolver = fileSystemResolver;
        _turnContextAccessor = turnContextAccessor;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
        _conversationGate = conversationGate;
    }

    public async Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken)
    {
        using var sessionContext = ConversationSessionContext.Push(
            request.Task.ProjectId,
            request.Task.ContextId,
            request.Task.Generation
        );
        var lease =
            _conversationGate == null
                ? null
                : await _conversationGate.AcquireAsync(
                    request.Task.ProjectConversationId,
                    request.Task.Generation,
                    cancellationToken
                );
        var ownership = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease?.HandleLostToken ?? CancellationToken.None
        );
        try
        {
            var result = await StartCoreAsync(request, ownership.Token);
            if (result.ActiveTurn == null)
            {
                if (lease != null)
                    await lease.DisposeAsync();
                ownership.Dispose();
            }
            else
            {
                _ = ReleaseExecutionLeaseAsync(result.Runtime!.WhenIdleAsync(), lease, ownership);
            }
            return result;
        }
        catch
        {
            if (lease != null)
                await lease.DisposeAsync();
            ownership.Dispose();
            throw;
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

    private async Task<RuntimeStartResult> StartCoreAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken
    )
    {
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await EnsureWorkspaceAsync(request.TurnContext.ProjectId, cancellationToken);

        switch (request.Command.AgentType)
        {
            case AgentRuntimeType.Agent:
            {
                var session = request.CurrentRuntime as AgentRuntime;
                if (
                    !CanReuseAgentSession(
                        session,
                        request.Task.ProjectId,
                        request.TurnContext.Settings,
                        request.Task.ContextId,
                        request.Task.Generation
                    )
                )
                {
                    await DisposeRuntimeAsync(request.CurrentRuntime);
                    session = await _agentRuntimeService.CreateRuntimeAsync(
                        request.AgentId,
                        request.Task,
                        request.TurnContext.Settings.ToCommand(),
                        cancellationToken
                    );
                }

                if (session == null)
                {
                    executionCts.Dispose();
                    return default;
                }

                if (!string.IsNullOrWhiteSpace(request.RequestedMode))
                {
                    await _agentRuntimeService.SetModeAsync(session, request.RequestedMode, cancellationToken);
                }

                var permissionState = new PermissionModeState(request.TurnContext.Settings.PermissionMode);
                permissionState.Register(session.Session);
                var coordinator = new HumanGateApprovalCoordinator(request.TurnContext.PendingHumanGateChanged);
                var approvalHandler = new PermissionAwareApprovalHandler(coordinator, permissionState);
                return StartTurn(
                    session,
                    request.TurnContext,
                    executionCts,
                    () =>
                    {
                        session.CancelActiveRequest();
                        coordinator.CancelAll();
                    },
                    ct =>
                        ExecuteAgentAsync(
                            session,
                            request.Command,
                            approvalHandler,
                            request.TurnContext.MessageSink,
                            ct
                        ),
                    coordinator.TrySubmitAsync,
                    (mode, _) =>
                    {
                        approvalHandler.SetPermissionMode(mode);
                        return ValueTask.CompletedTask;
                    }
                );
            }
            case AgentRuntimeType.Agentflow:
            {
                var session = request.CurrentRuntime as AgentflowRuntime;
                if (session == null)
                {
                    await DisposeRuntimeAsync(request.CurrentRuntime);
                    session = new AgentflowRuntime(
                        request.AgentId,
                        request.Task,
                        request.TurnContext.Settings.ToCommand(),
                        _agentflowRuntimeService
                    );
                }

                var permissionState = new PermissionModeState(request.TurnContext.Settings.PermissionMode);
                var coordinator = new HumanGateApprovalCoordinator(request.TurnContext.PendingHumanGateChanged);
                var approvalHandler = new PermissionAwareApprovalHandler(coordinator, permissionState);
                return StartTurn(
                    session,
                    request.TurnContext,
                    executionCts,
                    coordinator.CancelAll,
                    ct =>
                        ExecuteAgentflowAsync(
                            session,
                            request.Command,
                            approvalHandler,
                            permissionState,
                            request.TurnContext.MessageSink,
                            ct
                        ),
                    coordinator.TrySubmitAsync,
                    (mode, _) =>
                    {
                        approvalHandler.SetPermissionMode(mode);
                        return ValueTask.CompletedTask;
                    }
                );
            }
            default:
                executionCts.Dispose();
                return default;
        }
    }

    public static async Task<RuntimeBase?> DisposeRuntimeAsync(RuntimeBase? runtime)
    {
        if (runtime == null)
            return null;
        await runtime.DisposeAsync();
        return null;
    }

    public Task SetModeAsync(RuntimeBase runtime, string mode, CancellationToken cancellationToken)
    {
        if (runtime is not AgentRuntime agentRuntime)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The current execution target does not support mode changes."
            );
        }

        return _agentRuntimeService.SetModeAsync(agentRuntime, mode, cancellationToken);
    }

    public Task SetPermissionModeAsync(
        RuntimeBase runtime,
        PermissionMode permissionMode,
        CancellationToken cancellationToken
    )
    {
        if (runtime is AgentRuntime agentRuntime)
        {
            return _agentRuntimeService.SetPermissionModeAsync(agentRuntime, permissionMode, cancellationToken);
        }

        if (runtime is AgentflowRuntime agentflowRuntime)
        {
            agentflowRuntime.SetPermissionMode(permissionMode);
        }

        return Task.CompletedTask;
    }

    private async Task ExecuteAgentAsync(
        AgentRuntime session,
        ExecCommand command,
        IHumanGateApprovalHandler approvalHandler,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        using var interactionScope = _humanInteractionContextAccessor.Push(
            new ExecutionHumanInteractionChannel(approvalHandler, sink)
        );
        session.ResetCancellationToken();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.CancellationToken
        );
        var linkedToken = linkedCts.Token;
        var effectiveApprovalHandler = command.Stream
            ? approvalHandler
            : new MessageSinkApprovalHandler(approvalHandler, sink);
        var messages = command.Stream
            ? _agentRuntimeService.ExecuteStreamingAsync(session, command.Input, effectiveApprovalHandler, linkedToken)
            : ToAsyncEnumerable(() =>
                _agentRuntimeService.ExecuteAsync(session, command.Input, effectiveApprovalHandler, linkedToken)
            );
        await TurnPipeline.RunAsync(messages, command.Stream, sink, linkedToken);
    }

    private async Task ExecuteAgentflowAsync(
        AgentflowRuntime runtime,
        ExecCommand command,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        PermissionModeState permissionState,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        using var interactionScope = _humanInteractionContextAccessor.Push(
            new ExecutionHumanInteractionChannel(humanGateApprovalHandler, sink)
        );
        await TurnPipeline.RunAsync(
            runtime.ExecuteStreamingAsync(command, humanGateApprovalHandler, permissionState, cancellationToken),
            command.Stream,
            sink,
            cancellationToken
        );
    }

    private RuntimeStartResult StartTurn(
        RuntimeBase runtime,
        RuntimeTurnContext turnContext,
        CancellationTokenSource executionCts,
        Action interruptAction,
        Func<CancellationToken, Task> executeAsync,
        Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null,
        Func<PermissionMode, CancellationToken, ValueTask>? setPermissionModeAsync = null
    )
    {
        var activeTurn = runtime.StartTurn(
            turnContext,
            _turnContextAccessor,
            executionCts,
            interruptAction,
            executeAsync,
            submitHumanResponseAsync,
            setPermissionModeAsync
        );
        return new RuntimeStartResult(runtime, activeTurn);
    }

    internal static async IAsyncEnumerable<AgwMessage> ToAsyncEnumerable(
        Func<Task<IReadOnlyList<AgwMessage>>> messagesFactory
    )
    {
        var messages = await messagesFactory().ConfigureAwait(false);
        foreach (var message in messages)
        {
            yield return message;
        }

        await Task.CompletedTask;
    }

    private async Task EnsureWorkspaceAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var fs = await _fileSystemResolver.ResolveAsync(projectId, cancellationToken);
        if (fs == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, "Project was not found.");
        }
        if (await fs.StatAsync("", cancellationToken) == null)
        {
            await fs.CreateDirectoryAsync("", cancellationToken);
        }
    }

    /// <summary>
    /// 判断现有 Agent 运行时是否属于相同项目和归一化 context，从而允许复用会话。
    /// </summary>
    private static bool CanReuseAgentSession(
        AgentRuntime? session,
        Guid projectId,
        ExecutionSettings settings,
        string resolvedContextId,
        int generation
    )
    {
        if (session == null)
            return false;
        var contextId = ContextIdUtil.ResolveContextId(
            string.IsNullOrWhiteSpace(settings.ContextId) ? resolvedContextId : settings.ContextId
        );
        return string.Equals(session._contextId, contextId, StringComparison.Ordinal)
            && session._projectId == projectId
            && (session.SessionStateScope?.Generation ?? 0) == generation;
    }

    private sealed class MessageSinkApprovalHandler : IHumanGateApprovalHandler
    {
        private readonly IHumanGateApprovalHandler _inner;
        private readonly IExecutionMessageSink _sink;

        public MessageSinkApprovalHandler(IHumanGateApprovalHandler inner, IExecutionMessageSink sink)
        {
            _inner = inner;
            _sink = sink;
        }

        public bool RequiresHumanResponse(HumanGateApprovalRequest request) => _inner.RequiresHumanResponse(request);

        public async ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
            HumanGateApprovalRequest request,
            CancellationToken cancellationToken
        )
        {
            if (RequiresHumanResponse(request))
            {
                await _sink.WriteAsync(ToolApprovalSupport.CreateMessage(request), cancellationToken);
            }

            return await _inner.WaitForApprovalAsync(request, cancellationToken);
        }
    }
}
