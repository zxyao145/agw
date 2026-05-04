using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Application.Agents;
using Agw.Agents.Contracts;
using Agw.Agents.ExternalAgents;
using Agw.Domain.Services;
using Agw.Providers.Domain.Entities;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Models;
using Agw.Shared.Utils;

using Anthropic;

using ClaudeCodeSdk.MAF;

using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

using OpenAI;
using OpenAI.Chat;
using OpenAI.CodexSdk.MAF;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Application.AgentRun;

public class AgentRuntimeService : RuntimeServiceBase, IAgentRuntimeService
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

    public AgentRuntimeService(
        AgentAppService agentAppService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        HybridCache cache,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        IProjectTaskSessionBindingService projectTaskSessionBindingService,
        IWebHostEnvironment webHostEnvironment,
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
        _logger = logger;
    }

    public async Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        string? extraOverride = null,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
            ExtraOverride = extraOverride,
        }, cancellationToken);
    }

    private async Task<AIAgent?> CreateAiAgentAsync(CreateAiAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Agent);

        if (request.Agent.Type == AgentType.External)
        {
            TryCreateExternalAgent(request, out var externalAgent);
            return externalAgent;
        }

        return await CreateDefinitionAgentAsync(request.Agent, request.Workspace, cancellationToken).ConfigureAwait(false);
    }

    #region CreateExternalAgent

    private bool TryCreateExternalAgent(CreateAiAgentRequest request, [NotNullWhen(true)] out AIAgent? aiAgent)
    {
        aiAgent = null;
        if (request.Agent.Type != AgentType.External)
        {
            return false;
        }

        var extra = request.ExtraOverride ?? request.Agent.Extra;
        aiAgent = request.Agent.Name switch
        {
            AgentNames.ClaudeCode => CreateClaudeCodeAgent(
                extra,
                request.Workspace,
                request.TaskId,
                request.Resume,
                request.EnvironmentVariables),
            AgentNames.Codex => CreateCodexAgent(
                extra,
                request.Workspace,
                request.ProviderSessionId,
                request.Resume,
                request.EnvironmentVariables,
                request.OnExternalSessionStartedAsync),
            _ => null
        };
        return aiAgent != null;
    }

    private AIAgent? CreateClaudeCodeAgent(
        string? extra,
        string? workspace,
        Guid? taskId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            _logger.LogError("agent.Extra is null or whitespace");
            return null;
        }

        var options = JsonUtil.Deserialize<ClaudeCodeAIAgentOptions>(extra);
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            options = options with { WorkingDirectory = workspace };
        }

        if (taskId != null)
        {
            options = resume
                ? options with { Resume = taskId.Value.Normalize(), SessionId = null }
                : options with { Resume = null, SessionId = taskId };
        }

        options = AgentRuntimeServiceUtil.ApplyEnvironmentVariables(options, environmentVariables);
        options = options with { ChatHistoryProvider = _chatHistoryProvider };
        return new ClaudeCodeAIAgent(options, _logger);
    }

    private AIAgent? CreateCodexAgent(
        string? extra,
        string? workspace,
        Guid? threadId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            _logger.LogError("agent.Extra is null or whitespace");
            return null;
        }

        var options = AgentRuntimeServiceUtil.BuildCodexAIAgentOptions(
            extra,
            workspace,
            threadId,
            resume,
            environmentVariables,
            onThreadStartedAsync);
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        options = options with { ChatHistoryProvider = _chatHistoryProvider };
        return new CodexAIAgent(options, _logger);
    }


    #endregion


    #region DefinitionAgent

    private async Task<AIAgent?> CreateDefinitionAgentAsync(Agent agentDefinition, string? workspace, CancellationToken cancellationToken)
    {
        if (!agentDefinition.ModelProviderId.HasValue)
        {
            return null;
        }

        var runtimeConfiguration = await _agentAppService.GetModelRuntimeConfigurationAsync(agentDefinition.ModelProviderId.Value);
        if (runtimeConfiguration == null)
        {
            return null;
        }

        var model = runtimeConfiguration.Model;
        var provider = runtimeConfiguration.Provider;
        var authConfigs = provider.AuthConfigs.Where(x => x.Enable).ToList();
        if (authConfigs.Count == 0)
        {
            _logger.LogError("no auth config for provider:{ProviderName}", provider.Name);
            return null;
        }

        var authConfig = authConfigs[Random.Shared.Next(authConfigs.Count)];
        IList<AITool>? tools = await CreateAgentTools(agentDefinition, cancellationToken).ConfigureAwait(false);
        var skillsProvider = await CreateSkillsProviderAsync(agentDefinition.Id).ConfigureAwait(false);

        return provider.ProviderType switch
        {
            ProviderType.OpenAI => CreateOpenAiAgent(agentDefinition, model, provider, authConfig, tools, skillsProvider, workspace),
            ProviderType.Anthropic => CreateAnthropicAgent(agentDefinition, model, provider, authConfig, tools, skillsProvider, workspace),
            _ => throw new AgwException(ErrorCodes.UnsupportedProviderType, $"Provider type '{provider.ProviderType}' is not supported")
        };
    }

    private AIAgent CreateOpenAiAgent(
        Agent agentDefinition,
        LlmModel model,
        Provider provider,
        ProviderAuthConfig authConfig,
        IList<AITool>? tools,
        AIContextProvider? skillsProvider,
        string? workspace)
    {
        var apiKey = ResolveApiKey(authConfig);
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Endpoint),
        };
        var client = new OpenAIClient(credential, options);
        var chatCompletionClient = client.GetChatClient(model.Name);
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentDefinition.Name,
            Description = agentDefinition.Description,
            ChatHistoryProvider = _chatHistoryProvider,
            ChatOptions = new ChatOptions
            {
                ModelId = model.Name,
                Instructions = AgentRuntimeServiceUtil.BuildInstructions(agentDefinition.SystemPrompt, workspace),
                Tools = tools
            }
        };

        if (skillsProvider != null)
        {
            agentOptions.AIContextProviders = [skillsProvider];
        }

        return chatCompletionClient.AsAIAgent(agentOptions)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: provider.Name, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    private AIAgent CreateAnthropicAgent(
        Agent agentDefinition,
        LlmModel model,
        Provider provider,
        ProviderAuthConfig authConfig,
        IList<AITool>? tools,
        AIContextProvider? skillsProvider,
        string? workspace)
    {
        var anthropicClientOptions = new Anthropic.Core.ClientOptions
        {
            ApiKey = ResolveApiKey(authConfig),
            BaseUrl = provider.Endpoint
        };
        var client = new AnthropicClient(anthropicClientOptions);
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentDefinition.Name,
            Description = agentDefinition.Description,
            ChatHistoryProvider = _chatHistoryProvider,
            ChatOptions = new ChatOptions
            {
                ModelId = model.Name,
                Instructions = AgentRuntimeServiceUtil.BuildInstructions(agentDefinition.SystemPrompt, workspace),
                Tools = tools
            }
        };

        if (skillsProvider != null)
        {
            agentOptions.AIContextProviders = [skillsProvider];
        }

        return client.AsAIAgent(agentOptions)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: provider.Name, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    private string ResolveApiKey(ProviderAuthConfig authConfig)
    {
        if (authConfig.AuthType == ProviderAuthType.ApiKey)
        {
            return authConfig.ApiKey!;
        }

        var envVariableName = authConfig.EnvName!;
        var apiKeyFromEnv = Environment.GetEnvironmentVariable(envVariableName);
        if (string.IsNullOrWhiteSpace(apiKeyFromEnv))
        {
            _logger.LogError("Environment variable '{EnvName}' is not set or empty.", envVariableName);
            throw new AgwException(ErrorCodes.EnvironmentVariableNotSet, $"Environment variable '{envVariableName}' is not set or empty.");
        }

        return apiKeyFromEnv;
    }

    private async Task<IList<AITool>?> CreateAgentTools(Agent agent, CancellationToken cancellationToken)
    {
        var mergedTools = new List<AITool>();
        var registeredToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toolNames = await CollectNamedToolNamesAsync(agent.Id, agent.Tools).ConfigureAwait(false);
        if (toolNames.Length > 0)
        {
            var functions = _toolRegistry.CreateAIFunctions(toolNames);
            if (functions.Count > 0)
            {
                AddUniqueTools(mergedTools, registeredToolNames, functions);
            }
        }

        var mcpTools = await ListToolsByAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        if (mcpTools.Count > 0)
        {
            AddUniqueTools(mergedTools, registeredToolNames, mcpTools);
        }

        return mergedTools.Count > 0 ? mergedTools : null;
    }

    private static void AddUniqueTools(ICollection<AITool> destination, ISet<string> registeredToolNames, IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !registeredToolNames.Add(tool.Name))
            {
                continue;
            }

            destination.Add(tool);
        }
    }

    private async Task<string[]> CollectNamedToolNamesAsync(Guid agentId, string? rawAgentTools)
    {
        return await _agentAppService.CollectNamedToolNamesAsync(agentId, rawAgentTools);
    }

    private async Task<IReadOnlyList<McpClientTool>> ListToolsByAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var servers = await _agentAppService.ListEnabledMcpToolServersByAgentAsync(agentId);
        var tools = new List<McpClientTool>();
        foreach (var server in servers)
        {
            try
            {
                var serverTools = await McpToolServerToolClient.ListToolsAsync(server, cancellationToken).ConfigureAwait(false);
                if (serverTools.Count > 0)
                {
                    tools.AddRange(serverTools);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list MCP tools from server {ServerId}", server.Id);
            }
        }

        return tools;
    }

    private async Task<AIContextProvider?> CreateSkillsProviderAsync(Guid agentId)
    {
        var skills = await _agentAppService.ListSkillsByAgentAsync(agentId);
        var skillPaths = skills
            .Select(GetSkillAbsolutePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (skillPaths.Length == 0)
        {
            _logger.LogWarning("Agent {AgentId} has skill relations configured but no extracted skill directories were found.", agentId);
            return null;
        }

        return new AgentSkillsProvider(skillPaths: skillPaths);
    }

    private string GetSkillAbsolutePath(Skill skill)
    {
        if (!string.IsNullOrWhiteSpace(skill.ContentPath))
        {
            var normalizedPath = skill.ContentPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(GetWebRootPath(), normalizedPath);
        }

        return Path.Combine(GetWebRootPath(), "skills", skill.Name);
    }

    private string GetWebRootPath()
    {
        if (!string.IsNullOrWhiteSpace(_webHostEnvironment.WebRootPath))
        {
            return _webHostEnvironment.WebRootPath;
        }

        return Path.Combine(_webHostEnvironment.ContentRootPath, "wwwroot");
    }
    #endregion


    public async Task<AgentExecSession?> CreateSessionAsync(
        Guid agentId,
        ProjectTask task,
        SettingCommand settings,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        Guid projectId = task.ProjectId;
        var projectExtraSetting = await GetProjectExtraSettingAsync(projectId);
        var mergedExtra = MergeExtraSettings(agent.Extra, projectExtraSetting, settings.SettingContent);
        string taskIdString = task.Id.Normalize();
        var providerSessionId = await GetCodexProviderSessionIdAsync(agent, task.Id, cancellationToken);
        var resume = IsCodexExternalAgent(agent)
            ? providerSessionId.HasValue
            : settings.Resume;

        var resolvedContextId = string.IsNullOrWhiteSpace(task.ContextId)
            ? TaskUtil.GenContextId()
            : task.ContextId;
        var aiAgent = await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
            ExtraOverride = mergedExtra,
            Workspace = settings.Workspace,
            EnvironmentVariables = settings.EnvironmentVariables,
            TaskId = task.Id,
            ProviderSessionId = providerSessionId,
            ProjectId = projectId,
            Resume = resume,
            OnExternalSessionStartedAsync = CreateExternalSessionStartedCallback(agent, task),
        }, cancellationToken);
        if (aiAgent == null)
        {
            return null;
        }

        var agentSession = await GetOrCreateThreadAsync(agent, aiAgent, taskIdString, cancellationToken);
        _providerSessionState.InitializeSessionState(agentSession, resolvedContextId, taskIdString, ProjectDefaults.GetDefaultProjectIdentifier(projectId));
        return new AgentExecSession(
            aiAgent,
            agentSession,
            projectId: projectId,
            contextId: resolvedContextId,
            taskIdString,
            AgentRuntimeType.Agent,
            agentId,
            agent.Name,
            _logger,
            taskTitle: agent.Name);
    }

    private async Task<Guid?> GetCodexProviderSessionIdAsync(
        Agent agent,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        if (!IsCodexExternalAgent(agent))
        {
            return null;
        }

        var binding = await _projectTaskSessionBindingService.GetAsync(
            taskId,
            agent.Id,
            agent.Name,
            cancellationToken);
        if (binding == null)
        {
            return null;
        }

        return Guid.TryParse(binding.ProviderSessionId, out var providerSessionId)
            ? providerSessionId
            : null;
    }

    private Func<string, CancellationToken, ValueTask>? CreateExternalSessionStartedCallback(
        Agent agent,
        ProjectTask task)
    {
        if (!IsCodexExternalAgent(agent))
        {
            return null;
        }

        return async (providerSessionId, _) =>
        {
            try
            {
                await _projectTaskSessionBindingService.UpsertAsync(
                    task.Id,
                    agent.Id,
                    agent.Name,
                    providerSessionId,
                    "system",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to save provider session binding for task {TaskId}, agent {AgentId}.",
                    task.Id,
                    agent.Id);
            }
        };
    }

    private static bool IsCodexExternalAgent(Agent agent) =>
        agent.Type == AgentType.External
        && string.Equals(agent.Name, AgentNames.Codex, StringComparison.OrdinalIgnoreCase);

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
            ExtraOverride = mergedExtra
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
