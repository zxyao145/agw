using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Files.Abstracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Utils;


namespace Agw.Agents.Execution.Runtimes;

public readonly record struct RuntimeStartResult(RuntimeBase? Runtime, ActiveTurn? ActiveTurn);

public sealed record RuntimeStartRequest(
    Guid AgentId,
    TaskProjection Task,
    ExecCommand Command,
    RuntimeBase? CurrentRuntime,
    RuntimeTurnContext TurnContext);

public interface IRuntimeFactory
{
    Task<RuntimeStartResult> StartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken);
}

public sealed class RuntimeFactory : IRuntimeFactory
{
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly RuntimeTurnContextAccessor _turnContextAccessor;
    private readonly HumanInteractionContextAccessor _humanInteractionContextAccessor;

    public RuntimeFactory(
        IAgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService,
        IAgwFileSystemResolver fileSystemResolver,
        RuntimeTurnContextAccessor turnContextAccessor,
        HumanInteractionContextAccessor humanInteractionContextAccessor)
    {
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _fileSystemResolver = fileSystemResolver;
        _turnContextAccessor = turnContextAccessor;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
    }

    public async Task<RuntimeStartResult> StartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken)
    {
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await EnsureWorkspaceAsync(request.TurnContext.ProjectId, cancellationToken);

        switch (request.Command.AgentType)
        {
            case AgentRuntimeType.Agent:
                {
                    var session = request.CurrentRuntime as AgentRuntime;
                    if (!CanReuseAgentSession(session, request.TurnContext.Settings, request.Task.ContextId))
                    {
                        await DisposeRuntimeAsync(request.CurrentRuntime);
                        session = await _agentRuntimeService.CreateRuntimeAsync(
                            request.AgentId,
                            request.Task,
                            request.TurnContext.Settings.ToCommand(),
                            cancellationToken);
                    }

                    if (session == null)
                    {
                        executionCts.Dispose();
                        return default;
                    }

                    var coordinator = new HumanGateApprovalCoordinator(request.TurnContext.PendingHumanGateChanged);
                    return StartTurn(
                        session,
                        request.TurnContext,
                        executionCts,
                        () =>
                        {
                            session.CancelActiveRequest();
                            coordinator.CancelAll();
                        },
                        ct => ExecuteAgentAsync(
                            session,
                            request.Command,
                            coordinator,
                            request.TurnContext.MessageSink,
                            ct),
                        coordinator.TrySubmitAsync);
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
                            _agentflowRuntimeService);
                    }

                    var coordinator = new HumanGateApprovalCoordinator(request.TurnContext.PendingHumanGateChanged);
                    return StartTurn(
                        session,
                        request.TurnContext,
                        executionCts,
                        coordinator.CancelAll,
                        ct => ExecuteAgentflowAsync(
                            session,
                            request.Command,
                            coordinator,
                            request.TurnContext.MessageSink,
                            ct),
                        coordinator.TrySubmitAsync);
                }
            default:
                executionCts.Dispose();
                return default;
        }
    }

    public static async Task<RuntimeBase?> DisposeRuntimeAsync(RuntimeBase? runtime)
    {
        if (runtime == null) return null;
        await runtime.DisposeAsync();
        return null;
    }

    private async Task ExecuteAgentAsync(
        AgentRuntime session,
        ExecCommand command,
        IHumanGateApprovalHandler approvalHandler,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken)
    {
        using var interactionScope = _humanInteractionContextAccessor.Push(
            new ExecutionHumanInteractionChannel(approvalHandler, sink));
        session.ResetCancellationToken();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.CancellationToken);
        var linkedToken = linkedCts.Token;
        var effectiveApprovalHandler = command.Stream
            ? approvalHandler
            : new MessageSinkApprovalHandler(approvalHandler, sink);
        var messages = command.Stream
            ? _agentRuntimeService.ExecuteStreamingAsync(
                session,
                command.Input,
                effectiveApprovalHandler,
                linkedToken)
            : ToAsyncEnumerable(
                () => _agentRuntimeService.ExecuteAsync(
                    session,
                    command.Input,
                    effectiveApprovalHandler,
                    linkedToken));
        await TurnPipeline.RunAsync(
            messages,
            command.Stream,
            sink,
            linkedToken);
    }

    private async Task ExecuteAgentflowAsync(
        AgentflowRuntime runtime,
        ExecCommand command,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken)
    {
        using var interactionScope = _humanInteractionContextAccessor.Push(
            new ExecutionHumanInteractionChannel(humanGateApprovalHandler, sink));
        await TurnPipeline.RunAsync(
            runtime.ExecuteStreamingAsync(command, humanGateApprovalHandler, cancellationToken),
            command.Stream,
            sink,
            cancellationToken);
    }

    private RuntimeStartResult StartTurn(
        RuntimeBase runtime,
        RuntimeTurnContext turnContext,
        CancellationTokenSource executionCts,
        Action interruptAction,
        Func<CancellationToken, Task> executeAsync,
        Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null)
    {
        var activeTurn = runtime.StartTurn(
            turnContext,
            _turnContextAccessor,
            executionCts,
            interruptAction,
            executeAsync,
            submitHumanResponseAsync);
        return new RuntimeStartResult(runtime, activeTurn);
    }

    internal static async IAsyncEnumerable<AgwMessage> ToAsyncEnumerable(
        Func<Task<IReadOnlyList<AgwMessage>>> messagesFactory)
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
        ExecutionSettings settings,
        string resolvedContextId)
    {
        if (session == null) return false;
        var contextId = ContextIdUtil.ResolveContextId(
            string.IsNullOrWhiteSpace(settings.ContextId) ? resolvedContextId : settings.ContextId);
        return string.Equals(session._contextId, contextId, StringComparison.Ordinal)
               && session._projectId == ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId);
    }

    private sealed class MessageSinkApprovalHandler : IHumanGateApprovalHandler
    {
        private readonly IHumanGateApprovalHandler _inner;
        private readonly IExecutionMessageSink _sink;

        public MessageSinkApprovalHandler(
            IHumanGateApprovalHandler inner,
            IExecutionMessageSink sink)
        {
            _inner = inner;
            _sink = sink;
        }

        public async ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
            HumanGateApprovalRequest request,
            CancellationToken cancellationToken)
        {
            await _sink.WriteAsync(
                ToolApprovalSupport.CreateMessage(request),
                cancellationToken);
            return await _inner.WaitForApprovalAsync(request, cancellationToken);
        }
    }
}
