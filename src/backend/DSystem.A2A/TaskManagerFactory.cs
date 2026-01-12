using A2A;
using DSystem.Domain.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DSystem.A2A;

public class TaskManagerFactory
{
    private readonly IServiceProvider _sp;

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
    }

    public async Task<ITaskManager> GetTaskManager(HttpRequest request)
    {
        //var content = request.HttpContext;
        var id = request.RouteValues["agentId"]?.ToString() ?? null;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new BadHttpRequestException("agent id cannot be null or whiteSpace");
        }

        var taskManager = await GetTaskManager(id);
        return taskManager;
    }


    public async Task<ITaskManager> GetTaskManager(string agentId)
    {
        if (_taskManagers.ContainsKey(agentId))
        {
            return _taskManagers[agentId];
        }
        var taskManager = new TaskManager(_callbackHttpClient, _taskStore);
        taskManager.OnMessageReceived =
            async (MessageSendParams messageSendParams, CancellationToken cancellationToken)
            =>
            {
                var msg = await ProcessMessageAsync(agentId, messageSendParams, cancellationToken)
                .ConfigureAwait(false);
                return msg;
            };
        taskManager.OnAgentCardQuery =
            async (string agentUrl, CancellationToken cancellationToken) =>
            {
                using var scope = _sp.CreateScope();
                var sp = scope.ServiceProvider;
                var a2aAgentService = sp.GetRequiredService<A2AAgentService>();
                var agentCard = await a2aAgentService.GetAgentCardAsync(agentId);
                if (agentCard == null)
                {
                    throw new BadHttpRequestException("agent not found");
                }
                return agentCard;
            };
        return _taskManagers.GetOrAdd(agentId, taskManager);
    }

    /// <summary>
    /// Processes incoming messages and executes CLI commands safely.
    /// </summary>
    private async Task<A2AResponse> ProcessMessageAsync(
        string agentId,
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

        Console.WriteLine($"[CLI Agent] Received command: {userText}");


        try
        {
            var agentExecutionResult = await agentRunService
                .ExecuteAsync(Guid.Parse(agentId), "", userText);

            AgentMessage responseMessage;
            if (agentExecutionResult == null)
            {
                var errorText = $"agent not found for input: '{userText}'";

                responseMessage = new AgentMessage
                {
                    Role = MessageRole.Agent,
                    MessageId = Guid.NewGuid().ToString(),
                    ContextId = messageSendParams.Message.ContextId,
                    Parts = [ new TextPart { Text = errorText }]
                };
            }
            else
            {
                var parts = agentExecutionResult!.Messages.Select(x =>
                {
                    var textContent = x.Contents.Find(c => c.Type == "text");
                    return (new TextPart { Text = textContent?.Content?.ToString() ?? "" }) as Part;
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

            Console.WriteLine($"[CLI Agent] Command executed successfully");
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

            Console.WriteLine($"[CLI Agent] Error: {ex.Message}");
            return errorMessage;
        }
    }
}