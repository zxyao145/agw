using System.Net.WebSockets;

using Agw.Agents.Application.Agentflows;
using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Utils;

using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.Execution.CommandStrategies;

public readonly record struct ExecutionStartResult(AgentExecSession? AgentSession, ActiveTurn? ActiveTurn);

/// <summary>
/// Data needed to start an execution and stream its output back over the socket.
/// </summary>
public sealed record StreamingExecutionStartRequest(
    Guid AgentId,
    TaskProjection Task,
    ExecCommand Command,
    AgentExecSession? CurrentSession,
    SettingCommand Settings,
    WebSocket WebSocket,
    SemaphoreSlim SendLock);


internal sealed class ExecCommandStrategy : IExecutionCommandStrategy
{
    private readonly ILogger<ExecCommandStrategy> _logger;
    private readonly ITaskAppService _taskAppService;

    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly IAgwFileSystemResolver _fileSystemResolver;



    private const string BusyMessage = "The previous session is currently in progress, please wait and execute again.";


    public ExecCommandStrategy(ILogger<ExecCommandStrategy> logger,
        ITaskAppService taskAppService, IAgentRuntimeService agentRuntimeService, AgentflowRuntimeService agentflowRuntimeService, IAgwFileSystemResolver fileSystemResolver)
    {
        _logger = logger;
        _taskAppService = taskAppService;
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _fileSystemResolver = fileSystemResolver;
    }


    public bool CanHandle(AgentRunCommand command) => command is ExecCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var execCommand = (ExecCommand)command;
        if (context.ConnectionState.HasRunningExecution)
        {
            // Only one turn can stream on a connection at a time.
            await context.SendErrorAsync(BusyMessage);
            return default;
        }

        // Reuse the latest client settings when available; otherwise initialize a default execution context.
        var settings = context.ConnectionState.CurrentSettings ?? CreateDefaultSettings();
        if (context.ConnectionState.CurrentSettings == null)
        {
            context.ConnectionState.ApplySettings(settings);
        }

        // If settings changed while a session was idle, dispose the stale session before starting again.
        if (context.ConnectionState.ShouldRefreshSessionImmediately)
        {
            context.ConnectionState.ClearSession();
            context.AgentSession = await DisposeSessionAsync(context.AgentSession);
        }

        if (!context.ConnectionState.TryGetResolvedTask(settings, out var task))
        {
            // Keep task resolution in ExecCommandStrategy rather than SettingCommandStrategy because resolving can
            // create/validate execution state. A SettingCommand should only configure the socket; side effects belong
            // to the command that actually starts a run.
            // Resolve the task once per unchanged SettingCommand, creating it when the client is starting fresh.
            var taskResolution = await _taskAppService.ResolveTaskAsync(
                new ExecutionTaskRequest(
                    TaskId: null,
                    ProjectId: settings.ProjectId,
                    ContextId: settings.ContextId,
                    Input: AgwUserInputUtil.ExtractInputText(execCommand.Input),
                    Resume: settings.Resume,
                    User: context.CurrentUser),
                context.CancellationToken);
            var contextError = taskResolution.Error;
            if (contextError != null)
            {
                await context.CloseConnectionAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    context.ExtractReason(contextError) ?? "Invalid request payload");
                return new ExecutionCommandResult(CloseConnection: true);
            }

            task = taskResolution.Task!;
            context.ConnectionState.MarkTaskResolved(settings, task);
        }

        // Start the runtime and capture both the session and the active turn that will stream output.
        var executionStartResult = await StartStreamingExecutionAsync(
            new StreamingExecutionStartRequest(
                AgentId: context.AgentId,
                Task: task!,
                Command: execCommand,
                CurrentSession: context.AgentSession,
                Settings: settings,
                WebSocket: context.WebSocket,
                SendLock: context.SendLock),
            context.CancellationToken);
        var updatedSession = executionStartResult.AgentSession;
        var activeTurn = executionStartResult.ActiveTurn;
        context.AgentSession = updatedSession;

        if (activeTurn == null)
        {
            return default;
        }

        if (!context.ConnectionState.TryStartExecution(activeTurn))
        {
            // Another command managed to claim the connection before this turn was registered.
            await activeTurn.DisposeAsync();
            await context.SendErrorAsync(BusyMessage);
            return default;
        }

        if (execCommand.AgentType == AgentRuntimeType.Agent && context.AgentSession != null)
        {
            // For agent executions, mark the session snapshot that can be reused on the next request.
            context.ConnectionState.MarkSessionReady(settings);
        }

        // Fire-and-forget observation keeps the socket loop responsive while the turn streams in the background.
        context.ObserveTurn(activeTurn.ExecutionTask);
        return default;
    }

    private static SettingCommand CreateDefaultSettings()
    {
        return new SettingCommand(
            projectId: ProjectDefaults.DefaultBuiltInId,
            contextId: null);
    }

    private static async Task<AgentExecSession?> DisposeSessionAsync(AgentExecSession? agentSession)
    {
        if (agentSession == null)
        {
            return null;
        }

        agentSession.CancelActiveRequest();
        await agentSession.DisposeAsync();
        return null;
    }

    #region StartStreamingExecutionAsync

    public async Task<ExecutionStartResult> StartStreamingExecutionAsync(
        StreamingExecutionStartRequest request,
        CancellationToken cancellationToken)
    {
        // Each runtime type streams through the same socket but has slightly different session/cancellation rules.
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var projectId = request.Settings.ProjectId;
        var fs = await _fileSystemResolver.ResolveAsync(projectId, cancellationToken);
        var rootStat = await fs.StatAsync("", cancellationToken);
        if (rootStat == null)
        {
            await fs.CreateDirectoryAsync("", cancellationToken);
        }
        
        switch (request.Command.AgentType)
        {
            case AgentRuntimeType.Agent:
                {
                    var session = request.CurrentSession;
                    if (!CanReuseAgentSession(request.CurrentSession, request.Settings))
                    {
                        // Agent runs can reuse an existing session only when the task and project still match.
                        session = await DisposeSessionAsync(request.CurrentSession);

                        session = await _agentRuntimeService.CreateSessionAsync(
                            request.AgentId,
                            request.Task,
                            settings: request.Settings,
                            cancellationToken: cancellationToken);
                    }

                    if (session == null)
                    {
                        executionCts.Dispose();
                        return default;
                    }

                    // The runtime owns the session lifetime; the ActiveTurn just tracks stream completion and cancel.
                    var execTask = ExecuteAgentStreamingAsync(
                        session,
                        request.Command.Input,
                        request.WebSocket,
                        request.SendLock,
                        executionCts.Token);
                    return new ExecutionStartResult(
                        session,
                        new ActiveTurn(execTask, executionCts, session.CancelActiveRequest));
                }

            case AgentRuntimeType.Agentflow:
                {
                    var humanGateCoordinator = new HumanGateApprovalCoordinator();
                    // Agentflow does not carry a reusable agent session, so only the stream task and turn are tracked.
                    var execTask = ExecuteAgentflowStreamingAsync(
                        request.AgentId,
                        request.Command,
                        request.Settings,
                        request.Task.ContextId,
                        request.Task.TaskId,
                        humanGateCoordinator,
                        request.WebSocket,
                        request.SendLock,
                        executionCts.Token);
                    return new ExecutionStartResult(
                        request.CurrentSession,
                        new ActiveTurn(
                            execTask,
                            executionCts,
                            humanGateCoordinator.CancelAll,
                            humanGateCoordinator.TrySubmitAsync));
                }

            default:
                executionCts.Dispose();
                return default;
        }
    }

    private static bool CanReuseAgentSession(AgentExecSession? session, SettingCommand settings)
    {
        if (session == null)
        {
            return false;
        }

        var requestedContextId = settings.ContextId?.Trim();
        var requestedProjectId = ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId);

        return !string.IsNullOrWhiteSpace(requestedContextId)
            && string.Equals(session._contextId, requestedContextId, StringComparison.Ordinal)
            && session._projectId == requestedProjectId;
    }

    private async Task ExecuteAgentStreamingAsync(
        AgentExecSession session,
        AgwUserInput input,
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        session.ResetCancellationToken();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.CancellationToken);
        await foreach (var message in _agentRuntimeService.ExecuteStreamingAsync(
                           session,
                           input,
                           linkedCts.Token))
        {
            var json = JsonUtil.Serialize(message);
            await SendJsonAsync(webSocket, json, sendLock, linkedCts.Token);
        }
    }

    private async Task ExecuteAgentflowStreamingAsync(
        Guid id,
        ExecCommand request,
        SettingCommand settings,
        string? contextId,
        Guid taskId,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        await foreach (var message in _agentflowRuntimeService.ExecuteStreamingAsync(
                           agentflowId: id,
                           input: AgwUserInputUtil.ExtractAgentflowInputText(request.Input),
                           cancellationToken: cancellationToken,
                           projectId: ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId),
                           contextId: contextId,
                           taskId: taskId,
                           humanGateApprovalHandler: humanGateApprovalHandler))
        {
            var json = JsonUtil.Serialize(message);
            await SendJsonAsync(webSocket, json, sendLock, cancellationToken);
        }
    }

    private async Task SendJsonAsync(
        WebSocket webSocket,
        string json,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            var data = Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }
    #endregion
}
