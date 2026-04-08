using Agw.Agents.Application;
using Agw.Api.Contracts;
using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Agw.Shared.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;

namespace Agw.Api.Execution;

public sealed class AgentExecutionCoordinator(
    AgentRuntimeService agentRuntimeService,
    AgentflowRuntimeService agentflowRuntimeService,
    ITaskAppService taskAppService,
    IProjectAppService projectAppService,
    ILogger<AgentExecutionCoordinator> logger) : IAgentExecutionCoordinator
{
    private readonly AgentRuntimeService _agentRuntimeService = agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService = agentflowRuntimeService;
    private readonly ITaskAppService _taskAppService = taskAppService;
    private readonly IProjectAppService _projectAppService = projectAppService;
    private readonly ILogger<AgentExecutionCoordinator> _logger = logger;

    public async Task<SettingCommand> NormalizeSettingsAsync(SettingCommand settings, CancellationToken cancellationToken)
    {
        var normalizedSettings = new SettingCommand(settings.ProjectId, settings.TaskId, settings.Workspace, settings.SettingContent);
        if (await _taskAppService.HasTaskAsync(normalizedSettings.TaskId, cancellationToken: cancellationToken))
        {
            normalizedSettings.Resume = true;
        }

        return normalizedSettings;
    }

    public async Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
        ExecutionTaskRequest request,
        CancellationToken cancellationToken)
    {
        var resolvedProjectId = await _projectAppService.ResolveProjectIdAsync(request.ProjectId);
        if (!resolvedProjectId.HasValue)
        {
            return new ExecutionTaskResolutionResult(null, new BadRequestObjectResult("Project not found."));
        }

        if (request.Resume)
        {
            if (!request.TaskId.HasValue || request.TaskId.Value == Guid.Empty)
            {
                return new ExecutionTaskResolutionResult(null, new BadRequestObjectResult("TaskId is required when resume is true."));
            }

            var existingTask = await _taskAppService.GetTaskAsync(request.TaskId.Value);
            if (existingTask == null)
            {
                return new ExecutionTaskResolutionResult(null, new BadRequestObjectResult("Task not found."));
            }

            if (existingTask.ProjectId != resolvedProjectId.Value)
            {
                return new ExecutionTaskResolutionResult(null, new BadRequestObjectResult("Task does not belong to the supplied projectId."));
            }

            return new ExecutionTaskResolutionResult(existingTask, null);
        }

        if (!request.TaskId.HasValue || request.TaskId.Value == Guid.Empty)
        {
            return await CreateTaskAsync(
                resolvedProjectId.Value,
                null,
                request.Input,
                request.User,
                cancellationToken);
        }

        var task = await _taskAppService.GetTaskAsync(request.TaskId.Value);
        if (task == null)
        {
            return await CreateTaskAsync(
                resolvedProjectId.Value,
                request.TaskId,
                request.Input,
                request.User,
                cancellationToken);
        }

        if (task.ProjectId != resolvedProjectId.Value)
        {
            return new ExecutionTaskResolutionResult(null, new BadRequestObjectResult("Task does not belong to the supplied projectId."));
        }

        return new ExecutionTaskResolutionResult(task, null);
    }

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
                        new ActiveExecution(execTask, executionCts, session.CancelActiveRequest));
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
                        new ActiveExecution(execTask, executionCts));
                }

            default:
                executionCts.Dispose();
                return default;
        }
    }

    private async Task<ExecutionTaskResolutionResult> CreateTaskAsync(
        Guid projectId,
        Guid? taskId,
        string input,
        string user,
        CancellationToken cancellationToken)
    {
        var task = await _taskAppService.CreateTaskForExecutionAsync(
            projectId,
            taskId,
            input,
            user,
            cancellationToken);
        if (task == null)
        {
            return new ExecutionTaskResolutionResult(null, new BadRequestObjectResult("Failed to create task."));
        }

        return new ExecutionTaskResolutionResult(task, null);
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
                           ExecutionInputTextExtractor.ExtractAgentflowInputText(request.Input),
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
}
