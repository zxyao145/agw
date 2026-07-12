using System.Net.WebSockets;

using Agw.Agents.Application.Agentflows;
using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Tasks;


namespace Agw.Agents.Application.Execution;

public readonly record struct ExecutionStartResult(RuntimeExecSessionBase? RuntimeSession, ActiveTurn? ActiveTurn);

public sealed record StreamingExecutionStartRequest(
    Guid AgentId,
    TaskProjection Task,
    ExecCommand Command,
    RuntimeExecSessionBase? CurrentSession,
    SettingCommand Settings,
    IExecutionMessageSink MessageSink,
    Action<HumanGateApprovalRequest?>? PendingHumanGateChanged = null);

public interface IExecutionMessageSink
{
    ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken);
}

public sealed class WebSocketExecutionMessageSink : IExecutionMessageSink
{
    private readonly WebSocket _webSocket;
    private readonly SemaphoreSlim _sendLock;

    public WebSocketExecutionMessageSink(WebSocket webSocket, SemaphoreSlim sendLock)
    {
        _webSocket = webSocket;
        _sendLock = sendLock;
    }

    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        if (_webSocket.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_webSocket.State != WebSocketState.Open) return;
            var data = Encoding.UTF8.GetBytes(Agw.Shared.Utils.JsonUtil.Serialize(message));
            await _webSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

public sealed class ExecutionRuntimeStarter
{
    private readonly ITaskAppService _taskAppService;
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly IAgwFileSystemResolver _fileSystemResolver;

    public ExecutionRuntimeStarter(
        ITaskAppService taskAppService,
        IAgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService,
        IAgwFileSystemResolver fileSystemResolver)
    {
        _taskAppService = taskAppService;
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _fileSystemResolver = fileSystemResolver;
    }

    public Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
        ExecutionTaskRequest request,
        CancellationToken cancellationToken) =>
        _taskAppService.ResolveTaskAsync(request, cancellationToken);

    public async Task<ExecutionStartResult> StartAsync(
        StreamingExecutionStartRequest request,
        CancellationToken cancellationToken)
    {
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await EnsureWorkspaceAsync(request.Settings.ProjectId, cancellationToken);

        switch (request.Command.AgentType)
        {
            case AgentRuntimeType.Agent:
            {
                var session = request.CurrentSession as AgentExecSession;
                if (!CanReuseAgentSession(session, request.Settings, request.Task.ContextId))
                {
                    await DisposeSessionAsync(request.CurrentSession);
                    session = await _agentRuntimeService.CreateSessionAsync(
                        request.AgentId,
                        request.Task,
                        request.Settings,
                        cancellationToken);
                }

                if (session == null)
                {
                    executionCts.Dispose();
                    return default;
                }

                return StartTurn(
                    session,
                    executionCts,
                    session.CancelActiveRequest,
                    ct => ExecuteAgentAsync(
                        session,
                        request.Command,
                        request.MessageSink,
                        ct));
            }
            case AgentRuntimeType.Agentflow:
            {
                var session = request.CurrentSession as AgentflowExecSession;
                if (session == null)
                {
                    await DisposeSessionAsync(request.CurrentSession);
                    session = new AgentflowExecSession();
                }

                var coordinator = new HumanGateApprovalCoordinator(request.PendingHumanGateChanged);
                return StartTurn(
                    session,
                    executionCts,
                    coordinator.CancelAll,
                    ct => ExecuteAgentflowAsync(
                        request.AgentId,
                        request.Command,
                        request.Settings,
                        request.Task.ContextId,
                        request.Task.TaskId,
                        coordinator,
                        request.MessageSink,
                        ct),
                    coordinator.TrySubmitAsync);
            }
            default:
                executionCts.Dispose();
                return default;
        }
    }

    public static async Task<RuntimeExecSessionBase?> DisposeSessionAsync(RuntimeExecSessionBase? session)
    {
        if (session == null) return null;
        await session.DisposeAsync();
        return null;
    }

    private async Task ExecuteAgentAsync(
        AgentExecSession session,
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
        await ExecutionTurnRunner.RunAsync(
            messages,
            command.Stream,
            sink,
            linkedCts.Token);
    }

    private async Task ExecuteAgentflowAsync(
        Guid agentflowId,
        ExecCommand command,
        SettingCommand settings,
        string? contextId,
        Guid taskId,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken)
    {
        await ExecutionTurnRunner.RunAsync(
            _agentflowRuntimeService.ExecuteStreamingAsync(
                agentflowId,
                AgwUserInputUtil.ExtractAgentflowInputText(command.Input),
                cancellationToken,
                ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId),
                contextId,
                taskId,
                humanGateApprovalHandler),
            command.Stream,
            sink,
            cancellationToken);
    }

    private static ExecutionStartResult StartTurn(
        RuntimeExecSessionBase session,
        CancellationTokenSource executionCts,
        Action interruptAction,
        Func<CancellationToken, Task> executeAsync,
        Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionTask = RunAfterRegistrationAsync(start.Task, executeAsync, executionCts.Token);
        var activeTurn = new ActiveTurn(
            executionTask,
            executionCts,
            interruptAction,
            submitHumanResponseAsync);
        if (!session.TryStartTurn(activeTurn))
        {
            executionCts.Cancel();
            start.TrySetCanceled();
            _ = activeTurn.DisposeAsync();
            return new ExecutionStartResult(session, null);
        }

        start.SetResult();
        return new ExecutionStartResult(session, activeTurn);
    }

    private static async Task RunAfterRegistrationAsync(
        Task registration,
        Func<CancellationToken, Task> executeAsync,
        CancellationToken cancellationToken)
    {
        await registration;
        await executeAsync(cancellationToken);
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
        AgentExecSession? session,
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
