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
using ClaudeCodeSdk.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Agw.Api.Controllers;

[ApiController]
[Route("api/executions")]
public partial class AgentExecutionsController : ControllerBase
{
    private const int BufferSize = 1024 * 4;
    private const int MaxRequestBytes = 1024 * 64;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly ITaskAppService _taskAppService;
    private readonly IProjectAppService _projectAppService;
    private readonly ILogger<AgentExecutionsController> _logger;

    public AgentExecutionsController(
        AgentRuntimeService agentRuntimeService,
        AgentflowRuntimeService agentflowRuntimeService,
        ITaskAppService taskAppService,
        IProjectAppService projectAppService,
        ILogger<AgentExecutionsController> logger)
    {
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _taskAppService = taskAppService;
        _projectAppService = projectAppService;
        _logger = logger;
    }

    [HttpGet("{agentId:guid}/ws")]
    public async Task ExecuteWsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var sendLock = new SemaphoreSlim(1, 1);

        AgentExecSession? agentSession = null;
        Task? execTask = null;
        SettingCommand? settings = null;

        try
        {
            while (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var command = await ReceiveRequestAsync<AgentRunCommand>(webSocket, cancellationToken);
                if (command == null)
                {
                    await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Not Support Payload");
                    break;
                }

                switch (command)
                {
                    case SettingCommand settingRequest:
                        if (!IsJsonObject(settingRequest.SettingContent))
                        {
                            await SendErrorAsync(
                                webSocket,
                                "SettingContent must be a JSON object string.",
                                sendLock,
                                cancellationToken
                                );
                            break;
                        }

                        if(settingRequest != settings)
                        {
                            settings = settingRequest;
                            if (await _taskAppService.HasTaskAsync(settings.TaskId))
                            {
                                settings.Resume = true;
                            }

                            if (agentSession != null)
                            {
                                await agentSession.DisposeAsync();
                                agentSession = null;
                            }
                        }
                        
                        break;

                    case ExecCommand executionRequest:
                        if (settings == null)
                        {
                            settings = new SettingCommand
                                (
                                    projectId: ProjectDefaults.DefaultBuiltId,
                                    taskId: Guid.NewGuid()
                                );
                        }

                        if (execTask is { IsCompleted: false })
                        {
                            await SendErrorAsync(
                                webSocket, 
                                "A request is already in progress. Wait for it to complete before starting a new one.",
                                sendLock, 
                                cancellationToken
                                );
                            break;
                        }

                        var (task, contextError) = await ResolveTaskAsync(
                            agentId,
                            executionRequest.AgentType,
                            settings.TaskId,
                            settings.ProjectId,
                            ExtractAgentflowInputText(executionRequest.Input),
                            settings.SessionId,
                            settings.Resume);
                        if (contextError != null)
                        {
                            await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData,
                                ExtractReason(contextError) ?? "Invalid request payload");
                            return;
                        }
                        (agentSession, execTask) = await StartExecAsync(
                           agentId, task!, executionRequest, agentSession, settings,
                           webSocket, sendLock, cancellationToken);
                        if (execTask != null)
                        {
                            ObserveActiveExecTask(execTask);
                        }
                        break;

                    case InterruptCommand interruptRequest:
                        if (agentSession == null)
                        {
                            var message = string.IsNullOrWhiteSpace(interruptRequest.Reason)
                                ? "No active request is currently running."
                                : interruptRequest.Reason;
                            await SendSystemMessageAsync(webSocket, sendLock, message, cancellationToken);
                            break;
                        }
                        agentSession.CancelActiveRequest();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Operation Canceled");
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Request cancelled");
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "Unexpected WebSocketException");
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "WebSocket Error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in WebSocket handler");
            await SendErrorAsync(webSocket, $"Unexpected error: {ex.Message}", sendLock, cancellationToken);
            await TryCloseAsync(webSocket, WebSocketCloseStatus.InternalServerError, "Unexpected error");
        }
        finally
        {
            if (agentSession != null)
            {
                agentSession.CancelActiveRequest();
            }
            if (execTask != null)
            {
                await AwaitActiveExecTaskAsync(execTask);
            }
            if (agentSession != null)
            {
                await agentSession.DisposeAsync();
            }
            _logger.LogDebug("WebSocket connection closed");
        }
    }

    private void ObserveActiveExecTask(Task inputTask)
    {
        _ = inputTask.ContinueWith(
            task =>
            {
                if (task.IsCanceled) return;
                if (task.Exception != null)
                {
                    _logger.LogError(task.Exception, "Unhandled error while processing agent input");
                }
            },
            TaskScheduler.Default);
    }

    private async Task AwaitActiveExecTaskAsync(Task inputTask)
    {
        try { await inputTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while awaiting ClaudeCode input task");
        }
    }

    private static async Task<ActiveExecution?> ReleaseCompletedExecutionAsync(ActiveExecution? activeExecution)
    {
        if (activeExecution == null || !activeExecution.ExecutionTask.IsCompleted)
        {
            return activeExecution;
        }

        await DisposeActiveExecutionAsync(activeExecution, interruptIfRunning: false);
        return null;
    }

    private static async Task DisposeActiveExecutionAsync(
        ActiveExecution? activeExecution,
        bool interruptIfRunning)
    {
        if (activeExecution == null)
        {
            return;
        }

        if (interruptIfRunning && !activeExecution.ExecutionTask.IsCompleted)
        {
            activeExecution.RequestInterrupt(null);
        }

        await activeExecution.DisposeAsync();
    }

    private async Task<(AgentExecSession?, Task)> StartExecAsync(
        Guid id,
        ProjectTask task,
        ExecCommand request,
        AgentExecSession? currentSession,
        SettingCommand settings,
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        switch (request.AgentType)
        {
            case AgentRuntimeType.Agent:
                {
                    var session = currentSession;
                    if (!CanReuseAgentSession(currentSession, settings, task.ContextId))
                    {
                        if (currentSession != null)
                        {
                            await currentSession.DisposeAsync();
                        }

                        session = await _agentRuntimeService.CreateSessionAsync(
                            id,
                            task,
                            settings: settings,
                            cancellationToken: cancellationToken);
                    }

                    if (session == null)
                    {
                        executionCts.Dispose();
                        return (null, Task.CompletedTask);
                    }

                    Task execTask = ExecuteAgentStreamingAsync(session, request.Input, webSocket, sendLock, executionCts.Token);
                    return (session, execTask);
                }

            case AgentRuntimeType.Agentflow:
                {
                    Task execTask = ExecuteAgentflowStreamingAsync(id, request, settings, task.ContextId, webSocket, sendLock, executionCts.Token);
                    return (null, execTask);
                }

            default:
                executionCts.Dispose();
                return (null, Task.CompletedTask);
        }
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
                           ExtractAgentflowInputText(request.Input),
                           cancellationToken,
                           ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId),
                           contextId))
        {
            var json = JsonUtil.Serialize(message);
            await SendJsonAsync(webSocket, json, sendLock, cancellationToken);
        }
    }

    private async Task<T?> ReceiveRequestAsync<T>(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        using var stream = new MemoryStream();
        WebSocketReceiveResult? result;

        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Connection closed by client");
                return default;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidMessageType, "Invalid request payload");
                return default;
            }

            if (stream.Length + result.Count > MaxRequestBytes)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.MessageTooBig, "Request payload too large");
                return default;
            }

            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(stream.ToArray());
        try
        {
            var request = JsonUtil.Deserialize<T>(json);
            if (request == null)
            {
                await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid request payload");
                return default;
            }

            return request;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to deserialize WebSocket message: {Message}.", json);
            await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid request payload");
            return default;
        }
    }

    private async Task<(ProjectTask? task, IActionResult? error)> ResolveTaskAsync(
        Guid executionId,
        AgentRuntimeType agentType,
        Guid? taskId,
        Guid? projectId,
        string input,
        string? sessionId,
        bool resume = false)
    {
        var resolvedProjectId = await _projectAppService.ResolveProjectIdAsync(projectId);
        if (!resolvedProjectId.HasValue)
        {
            return (null, BadRequest("Project not found."));
        }

        if (resume)
        {
            if (!taskId.HasValue || taskId.Value == Guid.Empty)
            {
                return (null, BadRequest("TaskId is required when resume is true."));
            }

            var existingTask = await _taskAppService.GetTaskAsync(taskId.Value);
            if (existingTask == null)
            {
                return (null, BadRequest("Task not found."));
            }

            if (existingTask.ProjectId != resolvedProjectId.Value)
            {
                return (null, BadRequest("Task does not belong to the supplied projectId."));
            }

            return (existingTask, null);
        }

        if (!taskId.HasValue || taskId.Value == Guid.Empty)
        {
            return await CreateTaskAsync(
                executionId,
                agentType,
                resolvedProjectId.Value,
                null,
                input
                );
        }

        var task = await _taskAppService.GetTaskAsync(taskId.Value);
        if (task == null)
        {
            return await CreateTaskAsync(
                executionId,
                agentType,
                resolvedProjectId.Value,
                taskId,
                input
                );
        }
        else if (task.ProjectId != resolvedProjectId.Value)
        {
            return (null, BadRequest("Task does not belong to the supplied projectId."));
        }

        return (task, null);
    }

    private async Task<(ProjectTask? task, IActionResult? error)> CreateTaskAsync(
        Guid executionId,
        AgentRuntimeType agentType,
        Guid projectId,
        Guid? taskId,
        string input)
    {
        var user = User?.Identity?.Name ?? "system";
        var task = await _taskAppService.CreateTaskForExecutionAsync(
            projectId,
            taskId,
            agentType,
            executionId,
            input,
            user);
        if (task == null)
        {
            return (null, BadRequest("Failed to create task."));
        }

        return (task, null);
    }

    private static string ExtractReason(IActionResult result)
    {
        return result switch
        {
            ObjectResult objectResult when objectResult.Value is string message => message,
            StatusCodeResult statusCodeResult => $"Request failed with status {statusCodeResult.StatusCode}.",
            _ => "Invalid request payload"
        };
    }

    private static bool CanReuseAgentSession(
        AgentExecSession? session,
        SettingCommand settings,
        string? contextId)
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

    private static bool IsJsonObject(string settingContent)
    {
        if (string.IsNullOrWhiteSpace(settingContent))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(settingContent);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Task SendSystemMessageAsync(
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        string message,
        CancellationToken cancellationToken)
    {
        var payload = new AgwMessage(
            Guid.NewGuid().Normalize(),
            "$agw-server",
            AiRole.System,
            new List<AgwContent> { new AgwTextContent { Content = message } });
        return SendJsonAsync(webSocket, JsonUtil.Serialize(payload), sendLock, cancellationToken);
    }

    private static async Task SendJsonAsync(
        WebSocket webSocket,
        string json,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open) return;

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (webSocket.State != WebSocketState.Open) return;

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

    private static Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string reason)
    {
        return webSocket.State switch
        {
            WebSocketState.Open => webSocket.CloseAsync(status, reason, CancellationToken.None),
            WebSocketState.CloseReceived => webSocket.CloseOutputAsync(status, reason, CancellationToken.None),
            _ => Task.CompletedTask
        };
    }

    private static string ExtractAgentflowInputText(AgwUserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return string.Join(
            Environment.NewLine,
            input.Contents
                .Select(ExtractContentText)
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? ExtractContentText(AgwContent content)
    {
        return content switch
        {
            AgwTextContent text => text.Content,
            AgwTextReasoningContent textReasoning => textReasoning.Content,
            AgwErrorContent error => error.Content,
            AgwFunctionCallContent functionCall => functionCall.Content,
            AgwFunctionResultContent functionResult => functionResult.Content,
            AgwUriContent uri => uri.Uri.ToString(),
            _ => null
        };
    }

    private Task SendErrorAsync(
        WebSocket webSocket, 
        string errorMessage,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken
        ) =>
        SendJsonAsync(webSocket, CreateErrorMessage(errorMessage).Serialize(), sendLock, cancellationToken);


    private static AgwMessage CreateErrorMessage(string errorMessage)
    {
        var content = new AgwErrorContent
        {
            Content = errorMessage
        };

        var payload = new AgwMessage
            (
                Guid.NewGuid().Normalize(),
                "$agw-server",
                AiRole.System,
                new List<AgwContent> { content }
            );
        return payload;
    }
}
