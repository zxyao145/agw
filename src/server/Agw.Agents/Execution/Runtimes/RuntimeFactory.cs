using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Tasks;


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
    Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
        ExecutionTaskRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeStartResult> StartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken);
}

public sealed class RuntimeFactory : IRuntimeFactory
{
    private readonly ITaskAppService _taskAppService;
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly IRuntimeTurnContextAccessor _turnContextAccessor;

    public RuntimeFactory(
        ITaskAppService taskAppService,
        IAgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService,
        IAgwFileSystemResolver fileSystemResolver,
        IRuntimeTurnContextAccessor turnContextAccessor)
    {
        _taskAppService = taskAppService;
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _fileSystemResolver = fileSystemResolver;
        _turnContextAccessor = turnContextAccessor;
    }

    public Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
        ExecutionTaskRequest request,
        CancellationToken cancellationToken) =>
        _taskAppService.ResolveTaskAsync(request, cancellationToken);

    public async Task<RuntimeStartResult> StartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken)
    {
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await EnsureWorkspaceAsync(request.TurnContext.Settings.ProjectId, cancellationToken);

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
                        request.TurnContext.Settings,
                        cancellationToken);
                }

                if (session == null)
                {
                    executionCts.Dispose();
                    return default;
                }

                return StartTurn(
                    session,
                    request.TurnContext,
                    executionCts,
                    session.CancelActiveRequest,
                    ct => ExecuteAgentAsync(
                        session,
                        request.Command,
                        request.TurnContext.MessageSink,
                        ct));
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
                        request.TurnContext.Settings,
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
        IExecutionMessageSink sink,
        CancellationToken cancellationToken)
    {
        session.ResetCancellationToken();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.CancellationToken);
        var messages = command.Stream
            ? _agentRuntimeService.ExecuteStreamingAsync(session, command.Input, linkedCts.Token)
            : ToAsyncEnumerable(await _agentRuntimeService.ExecuteAsync(session, command.Input, linkedCts.Token));
        await TurnPipeline.RunAsync(
            messages,
            command.Stream,
            sink,
            linkedCts.Token);
    }

    private async Task ExecuteAgentflowAsync(
        AgentflowRuntime runtime,
        ExecCommand command,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken)
    {
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

    private static async IAsyncEnumerable<AgwMessage> ToAsyncEnumerable(
        IReadOnlyList<AgwMessage> messages)
    {
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

    private static bool CanReuseAgentSession(
        AgentRuntime? session,
        SettingCommand settings,
        string resolvedContextId)
    {
        if (session == null) return false;
        var contextId = string.IsNullOrWhiteSpace(settings.ContextId)
            ? resolvedContextId
            : settings.ContextId.Trim();
        return string.Equals(session._contextId, contextId, StringComparison.Ordinal)
               && session._projectId == ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId);
    }
}
