using A2A;
using Agw.Appliaction.Services.Agents;
using Agw.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Agw.A2A;

public class TaskManagerFactory
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<TaskManagerFactory> _logger;
    private readonly HttpClient? _callbackHttpClient = null;
    private readonly ITaskStore? _taskStore = null;

    private readonly ConcurrentDictionary<string, TaskManager> _taskManagers = new();

    public TaskManagerFactory(IServiceProvider sp,
        HttpClient? callbackHttpClient = null,
        ITaskStore? taskStore = null)
    {
        this._callbackHttpClient = callbackHttpClient;
        this._taskStore = taskStore;
        this._sp = sp;
        _logger = this._sp.GetRequiredService<ILoggerFactory>().CreateLogger<TaskManagerFactory>();
    }

    public async Task<ITaskManager> GetTaskManager(HttpRequest request)
    {
        //var content = request.HttpContext;
        var agentName = request.RouteValues["agentName"]?.ToString() ?? null;
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new BadHttpRequestException("agent id cannot be null or whiteSpace");
        }

        var taskManager = await GetTaskManager(agentName);
        return taskManager;
    }


    public async Task<ITaskManager> GetTaskManager(string agentName)
    {
        if (_taskManagers.ContainsKey(agentName))
        {
            return _taskManagers[agentName];
        }
        var taskManager = new TaskManager(_callbackHttpClient, _taskStore);
        taskManager.OnMessageReceived =
            async (MessageSendParams messageSendParams, CancellationToken cancellationToken)
            =>
            {
                var msg = await ProcessMessageAsync(agentName, messageSendParams, cancellationToken)
                .ConfigureAwait(false);
                return msg;
            };
        taskManager.OnAgentCardQuery =
            async (string agentUrl, CancellationToken cancellationToken) =>
            {
                using var scope = _sp.CreateScope();
                var sp = scope.ServiceProvider;
                var a2aAgentService = sp.GetRequiredService<A2AAgentService>();
                var agentCard = await a2aAgentService.GetAgentCardAsync(agentName);
                if (agentCard == null)
                {
                    throw new BadHttpRequestException("agent not found");
                }
                return agentCard;
            };
        return _taskManagers.GetOrAdd(agentName, taskManager);
    }

    /// <summary>
    /// Processes incoming messages and executes CLI commands safely.
    /// </summary>
    private async Task<A2AResponse> ProcessMessageAsync(
        string agentName,
        MessageSendParams messageSendParams,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var agentRunService = sp.GetRequiredService<AgentRuntimeService>();
        var userText = messageSendParams.Message
            .Parts?.OfType<TextPart>()
            .FirstOrDefault()
            ?.Text
            ?? string.Empty;

        _logger.LogInformation($"[CLI Agent] Received command: {userText}");


        try
        {
            var agentExecutionResult = await agentRunService
                .ExecuteByNameAsync(agentName, "", userText);

            AgentMessage responseMessage;
            if (agentExecutionResult == null)
            {
                var errorText = $"agent not found for input: '{userText}'";

                responseMessage = new AgentMessage
                {
                    Role = MessageRole.Agent,
                    MessageId = Guid.NewGuid().ToString(),
                    ContextId = messageSendParams.Message.ContextId,
                    Parts = [new TextPart { Text = errorText }]
                };
            }
            else
            {
                var parts = agentExecutionResult!.Messages.Select(x =>
                {
                    var textContent = x.Contents.Find(c => c is AgwTextContent);
                    return (new TextPart { Text = ExtractContentText(textContent) }) as Part;
                })
                     .ToList();

                responseMessage = new AgentMessage
                {
                    Role = MessageRole.Agent,
                    MessageId = Guid.NewGuid().ToString(),
                    ContextId = messageSendParams.Message.ContextId,
                    Parts = parts
                };
            }

            _logger.LogInformation($"[CLI Agent] Command executed successfully");
            _logger.LogDebug($"[CLI Agent] Command executed response:{responseMessage}", responseMessage);
            return responseMessage;
        }
        catch (Exception ex)
        {
            var errorText = $"Error executing agent '{userText}': {ex.Message}";

            var errorMessage = new AgentMessage
            {
                Role = MessageRole.Agent,
                MessageId = Guid.NewGuid().ToString(),
                ContextId = messageSendParams.Message.ContextId,
                Parts = [new TextPart { Text = errorText }]
            };

            _logger.LogError(ex, $"[CLI Agent] Error: {errorMessage}", errorMessage);
            return errorMessage;
        }
    }

    private static string ExtractContentText(AgwContent? content)
    {
        return content switch
        {
            AgwTextContent text => text.Content ?? string.Empty,
            AgwTextReasoningContent reasoning => reasoning.Content ?? string.Empty,
            AgwFunctionCallContent functionCall => functionCall.Content ?? string.Empty,
            AgwFunctionResultContent functionResult => functionResult.Content ?? string.Empty,
            AgwErrorContent error => error.Content ?? string.Empty,
            _ => string.Empty
        };
    }
}
