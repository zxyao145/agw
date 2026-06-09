using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Application.Agents;
using Agw.Agents.ExternalAgents;
using Agw.Domain.Services;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Models;

using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService : RuntimeServiceBase, IAgentRuntimeService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly AgentAppService _agentAppService;
    private readonly IProjectAppService _projectAppService;
    private readonly ToolRegistryService _toolRegistry;
    private readonly HybridCache _cache;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;
    private readonly IProjectTaskSessionBindingService _projectTaskSessionBindingService;
    private readonly IWebHostEnvironment _webHostEnvironment;
    private readonly IAgwFileSystemResolver _fileSystemResolver;

    public AgentRuntimeService(
        AgentAppService agentAppService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        HybridCache cache,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        IProjectTaskSessionBindingService projectTaskSessionBindingService,
        IWebHostEnvironment webHostEnvironment,
        IAgwFileSystemResolver fileSystemResolver,
        ILogger<AgentRuntimeService> logger)
    {
        _agentAppService = agentAppService;
        _projectAppService = projectAppService;
        _toolRegistry = toolRegistry;
        _cache = cache;
        _chatHistoryProvider = chatHistoryProvider;
        _providerSessionState = providerSessionState;
        _projectTaskSessionBindingService = projectTaskSessionBindingService;
        _webHostEnvironment = webHostEnvironment;
        _fileSystemResolver = fileSystemResolver;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(AgentExecSession session, AgwUserInput input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            await foreach (var message in session.ExecuteStreamingAsync(input, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }

            yield return CreateTurnFinishedMessage(cancellationToken);
        }
        finally
        {
            await SaveSessionThreadStateAsync(session._taskId, session.Agent, session.Session, cancellationToken);
        }
    }

    public async Task<AgentExecutionResult?> ExecuteByNameAsync(AgentExecuteByNameRequest request, CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentByNameAsync(request.AgentName);
        if (agent == null)
        {
            return null;
        }

        var req = new AgentExecuteRequest
        {
            Agent = agent,
            Input = request.Input,
            TaskId = request.TaskId,
            ProjectId = request.ProjectId,
            ContextId = request.ContextId,
        };

        return await ExecuteAsync(req, cancellationToken);
    }

    public async Task<AgentExecutionResult?> ExecuteByIdAsync(AgentExecuteByIdRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _agentAppService.GetAgentAsync(request.AgentId);
        if (agent == null)
        {
            return null;
        }
        var req = new AgentExecuteRequest
        {
            Agent = agent,
            Input = request.Input,
            TaskId = request.TaskId,
            ProjectId = request.ProjectId,
            ContextId = request.ContextId,
        };
        return await ExecuteAsync(req, cancellationToken);
    }

    private async Task<AgentExecutionResult?> ExecuteAsync(AgentExecuteRequest request, CancellationToken cancellationToken = default)
    {
        Guid? taskId = request.TaskId;
        List<ChatMessage> chatMsg = request.Input;
        Guid? projectId = request.ProjectId;
        string? contextId = request.ContextId;
        Agent agent = request.Agent;


        projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        var projectExtraSetting = await GetProjectExtraSettingAsync(projectId);
        var mergedExtra = MergeExtraSettings(agent.Extra, projectExtraSetting, null);
        var aiAgent = await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
            ExtraOverride = mergedExtra,
            ProjectId = projectId
        }, cancellationToken);
        if (aiAgent == null)
        {
            throw new AgwException(ErrorCodes.AiAgentCreationFailed);
        }

        try
        {
            var session = await CreateOrRestoreSessionAsync(aiAgent, taskId).ConfigureAwait(false);
            taskId ??= Guid.NewGuid();
            string taskIdValue = taskId.Value.Normalize();

            _providerSessionState.InitializeSessionState(
                session,
                string.IsNullOrWhiteSpace(contextId) ? taskIdValue : contextId,
                taskIdValue,
                ProjectDefaults.GetDefaultProjectIdentifier(projectId)
                );

            var messages = await CollectStreamingMessagesAsync(aiAgent, chatMsg, session).ConfigureAwait(false);

            return new AgentExecutionResult(taskIdValue, messages);
        }
        finally
        {
            if (aiAgent is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (aiAgent is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }


    private async Task<AgentSession> GetOrCreateThreadAsync(
        Agent agent,
        AIAgent aiAgent,
        string taskId,
        CancellationToken cancellationToken)
    {
        if (agent.Type == AgentType.External)
        {
            return await aiAgent.CreateSessionAsync(cancellationToken);
        }

        var value = await _cache.GetOrCreateAsync<string>(
            taskId,
            _ => ValueTask.FromResult(string.Empty),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            return await aiAgent.CreateSessionAsync();
        }

        try
        {
            var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
            return await aiAgent.DeserializeSessionAsync(serializedThread);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Thread cache deserialization failed for task: {TaskId}. A new thread will be created.", taskId);
            return await aiAgent.CreateSessionAsync(cancellationToken);
        }
    }

    private async Task SaveSessionThreadStateAsync(string taskId, AIAgent aiAgent, AgentSession session, CancellationToken cancellationToken)
    {
        var ele = await aiAgent.SerializeSessionAsync(session);
        var serialized = JsonSerializer.Serialize(ele);
        await _cache.SetAsync(taskId, serialized, cancellationToken: cancellationToken);
    }

    private string? MergeExtraSettings(string? agentExtra, string? projectExtraSetting, string? requestExtraSetting) =>
        AgentRuntimeServiceUtil.MergeExtraSettings(
            agentExtra,
            projectExtraSetting,
            requestExtraSetting,
            settingName => _logger.LogWarning("{SettingName} is not a valid JSON object. Skipping it.", settingName));

    private Task<string?> GetProjectExtraSettingAsync(Guid? projectId)
    {
        return _projectAppService.GetProjectExtraSettingAsync(projectId);
    }


    private async Task<AgentSession> CreateOrRestoreSessionAsync(AIAgent aiAgent, Guid? taskId)
    {
        if (taskId == null)
        {
            return await aiAgent.CreateSessionAsync().ConfigureAwait(false);
        }

        var value = await _cache.GetOrCreateAsync<string>(taskId.Value.Normalize(), _ => ValueTask.FromResult(string.Empty)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
        {
            return await aiAgent.CreateSessionAsync().ConfigureAwait(false);
        }

        var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
        return await aiAgent.DeserializeSessionAsync(serializedThread).ConfigureAwait(false);
    }

    private static async Task<List<AgwMessage>> CollectStreamingMessagesAsync(
        AIAgent aiAgent,
        IReadOnlyList<ChatMessage> chatMessages,
        AgentSession session)
    {
        var stream = aiAgent.RunStreamingAsync(chatMessages, session);
        var messages = new List<AgwMessage>();
        await foreach (var update in stream)
        {
            var msg = update.ToAiMessage();
            if (msg != null)
            {
                messages.Add(msg);
            }
        }

        return messages;
    }
}
