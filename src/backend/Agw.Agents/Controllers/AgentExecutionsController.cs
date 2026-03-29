using Agw.Agents.Application;
using Agw.Api.Contracts;
using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Agw.Shared.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    [HttpGet("{id:guid}/ws")]
    public async Task ExecuteWsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var sendLock = new SemaphoreSlim(1, 1);

        ActiveExecution? activeExecution = null;
        AgentExecSession? agentSession = null;
        SettingRequest? settings = null;

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
                    case SettingRequest settingRequest:
                        if (!IsJsonObject(settingRequest.SettingContent))
                        {
                            await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData,
                                "SettingContent must be a JSON object string.");
                            return;
                        }
                        settings = settingRequest;
                        if (agentSession != null)
                        {
                            await agentSession.DisposeAsync();
                            agentSession = null;
                        }
                        break;

                    case ExecRequest executionRequest:
                        activeExecution = await ReleaseCompletedExecutionAsync(activeExecution);
                        if (activeExecution != null) break;

                        var (task, contextError) = await ResolveTaskAsync(executionRequest.TaskId, executionRequest.ProjectId);
                        if (contextError != null)
                        {
                            await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData,
                                ExtractReason(contextError) ?? "Invalid request payload");
                            return;
                        }

                        var (execution, session, error) = await StartExecAsync(
                            id, task, executionRequest, agentSession, settings,
                            webSocket, sendLock, cancellationToken);

                        if (execution == null)
                        {
                            await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData,
                                error ?? "Invalid request payload");
                            return;
                        }

                        activeExecution = execution;
                        agentSession = session;
                        break;

                    case InterruptRequest interruptRequest:
                        if (activeExecution == null)
                        {
                            var message = string.IsNullOrWhiteSpace(interruptRequest.Reason)
                                ? "No active request is currently running."
                                : interruptRequest.Reason;
                            await SendSystemMessageAsync(webSocket, sendLock, message, cancellationToken);
                            break;
                        }
                        activeExecution.RequestInterrupt(interruptRequest.Reason);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Request cancelled");
        }
        catch (WebSocketException)
        {
            await TryCloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "WebSocket Error");
            activeExecution?.RequestInterrupt(null);
        }
        finally
        {
            await DisposeActiveExecutionAsync(activeExecution, interruptIfRunning: true);
            if (agentSession != null)
            {
                await agentSession.DisposeAsync();
            }
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

    private async Task<(ActiveExecution? execution, AgentExecSession? session, string? error)> StartExecAsync(
        Guid id,
        ProjectTask? task,
        ExecRequest request,
        AgentExecSession? currentSession,
        SettingRequest? settings,
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        switch (request.AgentType)
        {
            case AgentRuntimeType.Agent:
                var session = currentSession;
                if (!CanReuseAgentSession(currentSession, request, task?.ContextId))
                {
                    if (currentSession != null)
                    {
                        await currentSession.DisposeAsync();
                    }

                    session = await _agentRuntimeService.CreateSessionAsync(
                        id,
                        request.SessionId ?? string.Empty,
                        request.ProjectId,
                        task?.ContextId,
                        extraSetting: settings?.SettingContent,
                        cancellationToken: cancellationToken);
                }

                if (session == null)
                {
                    executionCts.Dispose();
                    return (null, null, "Agent not found.");
                }

                return (
                    new ActiveExecution(
                        ExecuteAgentStreamingAsync(session, request.Input, webSocket, sendLock, executionCts.Token),
                        executionCts,
                        session),
                    session,
                    null);

            case AgentRuntimeType.Agentflow:
                return (
                    new ActiveExecution(
                        ExecuteAgentflowStreamingAsync(id, request, task?.ContextId, webSocket, sendLock, executionCts.Token),
                        executionCts),
                    currentSession,
                    null);

            default:
                executionCts.Dispose();
                return (null, currentSession, "Invalid AgentType.");
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
        ExecRequest request,
        string? contextId,
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        await foreach (var message in _agentflowRuntimeService.ExecuteStreamingAsync(
                           id,
                           request.SessionId ?? string.Empty,
                           ExtractAgentflowInputText(request.Input),
                           cancellationToken,
                           ProjectDefaults.GetDefaultProjectIdentifier(request.ProjectId),
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
        catch (JsonException e)
        {
            _logger.LogError(e, "Failed to deserialize WebSocket message: {Message}.", json);
            await TryCloseAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid request payload");
            return default;
        }
    }

    private async Task<(ProjectTask? task, IActionResult? error)> ResolveTaskAsync(Guid? taskId, Guid? projectId)
    {
        if (!taskId.HasValue)
        {
            return (null, null);
        }

        var task = await _taskAppService.GetTaskAsync(taskId.Value);
        if (task == null)
        {
            return (null, NotFound("Task not found."));
        }

        if (projectId != null)
        {
            var resolvedProjectId = await _projectAppService.ResolveProjectIdAsync(projectId);
            if (!resolvedProjectId.HasValue || task.ProjectId != resolvedProjectId.Value)
            {
                return (null, BadRequest("Task does not belong to the supplied projectId."));
            }
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
        ExecRequest request,
        string? contextId)
    {
        if (session == null)
        {
            return false;
        }

        var requestedSessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? session._sessionId
            : request.SessionId;
        var requestedProjectId = ProjectDefaults.GetDefaultProjectIdentifier(request.ProjectId);
        var requestedContextId = string.IsNullOrWhiteSpace(contextId) ? requestedSessionId : contextId;

        return session._sessionId == requestedSessionId
            && session._projectId == requestedProjectId
            && session._contextId == requestedContextId;
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
            Guid.NewGuid().ToString(),
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
}
