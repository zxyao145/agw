using System.Net.WebSockets;

using Agw.Agents.Application.Agentflows;
using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
using Agw.Shared.Models;
using Agw.Shared.Utils;

using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.Execution.CommandStrategies;

public readonly record struct ExecutionStartResult(AgentExecSession? AgentSession, ActiveTurn? ActiveTurn);

public sealed record StreamingExecutionStartRequest(
    Guid AgentId,
    ProjectTask Task,
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


    private const string BusyMessage = "The previous session is currently in progress, please wait and execute again.";


    public ExecCommandStrategy(ILogger<ExecCommandStrategy> logger,
        ITaskAppService taskAppService, IAgentRuntimeService agentRuntimeService, AgentflowRuntimeService agentflowRuntimeService)
    {
        _logger = logger;
        _taskAppService = taskAppService;
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
    }


    public bool CanHandle(AgentRunCommand command) => command is ExecCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var execCommand = (ExecCommand)command;
        if (context.ConnectionState.HasRunningExecution)
        {
            await context.SendErrorAsync(BusyMessage);
            return default;
        }

        var settings = context.ConnectionState.CurrentSettings ?? CreateDefaultSettings();
        if (context.ConnectionState.CurrentSettings == null)
        {
            context.ConnectionState.ApplySettings(settings);
        }

        if (context.ConnectionState.ShouldRefreshSessionImmediately)
        {
            context.ConnectionState.ClearSession();
            context.AgentSession = await DisposeSessionAsync(context.AgentSession);
        }

        // get existing ProjectTask or create a new ProjectTask
        var taskResolution = await _taskAppService.ResolveTaskAsync(
            new ExecutionTaskRequest(
                ExecutionId: context.AgentId,
                AgentType: execCommand.AgentType,
                TaskId: settings.TaskId,
                ProjectId: settings.ProjectId,
                Input: AgwUserInputUtil.ExtractAgentflowInputText(execCommand.Input),
                Resume: settings.Resume,
                User: context.CurrentUser),
            context.CancellationToken);
        var task = taskResolution.Task;
        var contextError = taskResolution.Error;
        if (contextError != null)
        {
            await context.CloseConnectionAsync(
                WebSocketCloseStatus.InvalidPayloadData,
                context.ExtractReason(contextError) ?? "Invalid request payload");
            return new ExecutionCommandResult(CloseConnection: true);
        }


        // Start streaming execution based on agentType branch
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
            await activeTurn.DisposeAsync();
            await context.SendErrorAsync(BusyMessage);
            return default;
        }

        if (execCommand.AgentType == AgentRuntimeType.Agent && context.AgentSession != null)
        {
            context.ConnectionState.MarkSessionReady(settings);
        }

        context.ObserveTurn(activeTurn.ExecutionTask);
        return default;
    }

    private static SettingCommand CreateDefaultSettings()
    {
        return new SettingCommand(
            projectId: ProjectDefaults.DefaultBuiltInId,
            taskId: Guid.NewGuid()
            );
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
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        switch (request.Command.AgentType)
        {
            case AgentRuntimeType.Agent:
                {
                    var session = request.CurrentSession;
                    if (!CanReuseAgentSession(request.CurrentSession, request.Settings))
                    {
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
                    var execTask = ExecuteAgentflowStreamingAsync(
                        request.AgentId,
                        request.Command,
                        request.Settings,
                        request.Task.ContextId,
                        request.WebSocket,
                        request.SendLock,
                        executionCts.Token);
                    return new ExecutionStartResult(
                        request.CurrentSession,
                        new ActiveTurn(execTask, executionCts));
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

        var requestedTaskId = settings.TaskId.Normalize();
        var requestedProjectId = ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId);

        return session._taskId == requestedTaskId
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
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        await foreach (var message in _agentflowRuntimeService.ExecuteStreamingAsync(
                           id,
                           AgwUserInputUtil.ExtractAgentflowInputText(request.Input),
                           cancellationToken,
                           ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId),
                           contextId))
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
