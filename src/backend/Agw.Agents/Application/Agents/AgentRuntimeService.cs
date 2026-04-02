using Agw.Agents.Application;
using Agw.Agents.ExternalAgents;
using Agw.Api.Contracts;
using Agw.Appliaction.ExternalAgents;
using Agw.Domain.Entities;
using Agw.Domain.Services;
using Agw.Domain.Services.Agents;
using Agw.Shared;
using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Agw.Shared.Utils;
using Anthropic;
using ClaudeCodeSdk.MAF;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Appliaction.Services.Agents;

public record AgentExecutionResult(
    string TaskId,
    IReadOnlyList<AgwMessage> Messages);

public class AgentRuntimeService: RuntimService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<ModelProvider> _modelProviderRepository;
    private readonly IRepository<McpToolServer> _mcpToolServerRepository;
    private readonly IRepository<AgentMcpToolServer> _agentMcpToolServerRepository;
    private readonly IRepository<Skill> _skillRepository;
    private readonly IRepository<AgentSkillRelation> _agentSkillRelationRepository;
    private readonly IRepository<LlmModel> _modelRepository;
    private readonly IRepository<Provider> _providerRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AgentDomainService _agentDomainService;
    private readonly McpToolServerDomainService _mcpToolServerDomainService;
    private readonly IProjectAppService _projectAppService;
    private readonly ToolRegistryService _toolRegistry;
    private readonly HybridCache _cache;
    private readonly ITaskAppService _taskRecordApplication;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;
    private readonly IWebHostEnvironment _webHostEnvironment;

    public AgentRuntimeService(
        IRepository<Agent> agentRepository,
        IRepository<ModelProvider> modelProviderRepository,
        IRepository<McpToolServer> mcpToolServerRepository,
        IRepository<AgentMcpToolServer> agentMcpToolServerRepository,
        IRepository<Skill> skillRepository,
        IRepository<AgentSkillRelation> agentSkillRelationRepository,
        IRepository<LlmModel> modelRepository,
        IRepository<Provider> providerRepository,
        IUnitOfWork unitOfWork,
        AgentDomainService agentDomainService,
        McpToolServerDomainService mcpToolServerDomainService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        HybridCache cache,
        ITaskAppService taskRecordApplication,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        IWebHostEnvironment webHostEnvironment,
        ILogger<AgentRuntimeService> logger)
    {
        _agentRepository = agentRepository;
        _modelProviderRepository = modelProviderRepository;
        _mcpToolServerRepository = mcpToolServerRepository;
        _agentMcpToolServerRepository = agentMcpToolServerRepository;
        _skillRepository = skillRepository;
        _agentSkillRelationRepository = agentSkillRelationRepository;
        _modelRepository = modelRepository;
        _providerRepository = providerRepository;
        _unitOfWork = unitOfWork;
        _agentDomainService = agentDomainService;
        _mcpToolServerDomainService = mcpToolServerDomainService;
        _projectAppService = projectAppService;
        _toolRegistry = toolRegistry;
        _cache = cache;
        _taskRecordApplication = taskRecordApplication;
        _chatHistoryProvider = chatHistoryProvider;
        _providerSessionState = providerSessionState;
        _webHostEnvironment = webHostEnvironment;
        _logger = logger;
    }

    public Task<IReadOnlyList<Agent>> ListAgentsAsync() =>
        _agentRepository.ListAsync(null, x => x.AgentMcpToolServers, x => x.AgentSkillRelations);

    public async Task<Agent?> GetAgentAsync(Guid id)
    {
        var matches = await _agentRepository.ListAsync(
            x => x.Id == id,
            x => x.AgentMcpToolServers,
            x => x.AgentSkillRelations);
        return matches.FirstOrDefault();
    }

    public async Task<Agent?> CreateAgentAsync(
        Agent agent,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        string user)
    {
        if (await HasInvalidModelProviderAsync(agent.ModelProviderId))
        {
            return null;
        }

        _agentDomainService.PrepareForCreate(agent, user);
        await _agentRepository.AddAsync(agent);
        await SyncAgentMcpToolServerRelationsAsync(agent.Id, mcpToolServerIds);
        await SyncAgentSkillRelationsAsync(agent.Id, skillIds);
        await _unitOfWork.SaveChangesAsync();
        return agent;
    }

    public async Task<Agent?> UpdateAgentAsync(
        Guid id,
        Action<Agent> updateAction,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        string user)
    {
        var existing = await _agentRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        _agentDomainService.ApplyUpdate(existing, updateAction, user);
        if (await HasInvalidModelProviderAsync(existing.ModelProviderId))
        {
            return null;
        }

        _agentRepository.Update(existing);
        await SyncAgentMcpToolServerRelationsAsync(existing.Id, mcpToolServerIds);
        await SyncAgentSkillRelationsAsync(existing.Id, skillIds);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAgentAsync(Guid id)
    {
        var existing = await _agentRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        var skillRelations = await _agentSkillRelationRepository.ListAsync(x => x.AgentId == id);
        foreach (var relation in skillRelations)
        {
            _agentSkillRelationRepository.Remove(relation);
        }

        _agentRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public Task<IReadOnlyList<McpToolServer>> ListMcpToolServersAsync() => _mcpToolServerRepository.ListAsync();

    public Task<McpToolServer?> GetMcpToolServerAsync(Guid id) => _mcpToolServerRepository.GetByIdAsync(id);

    public async Task<McpToolServer> CreateMcpToolServerAsync(McpToolServer server, IEnumerable<Guid>? agentIds, string user)
    {
        _mcpToolServerDomainService.PrepareForCreate(server, user);
        await _mcpToolServerRepository.AddAsync(server);
        await SyncMcpToolServerAgentRelationsAsync(server.Id, agentIds);
        await _unitOfWork.SaveChangesAsync();
        return server;
    }

    public async Task<McpToolServer?> UpdateMcpToolServerAsync(Guid id, Action<McpToolServer> updateAction, string user)
    {
        var existing = await _mcpToolServerRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        _mcpToolServerDomainService.ApplyUpdate(existing, updateAction, user);
        _mcpToolServerRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteMcpToolServerAsync(Guid id)
    {
        var existing = await _mcpToolServerRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _mcpToolServerRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<McpClientTool>> ListMcpToolsAsync(Guid mcpToolServerId, CancellationToken cancellationToken = default)
    {
        var server = await _mcpToolServerRepository.GetByIdAsync(mcpToolServerId);
        if (server == null || !server.Enabled)
        {
            return [];
        }

        return await ListToolsAsync(server, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        string? systemPrompt = null,
        string? extraOverride = null,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(agent, extraOverride, cancellationToken: cancellationToken);
    }

    public async Task<AIAgent?> CreateAiAgentAsync(
        Agent agent,
        string? extraOverride = null,
        Guid? taskId = null,
        Guid? projectId = null,
        bool resume = false,
        CancellationToken cancellationToken = default)
    {
        if (agent.Type == AgentType.External)
        {
            var extra = extraOverride ?? agent.Extra;
            if (agent.Name == "ClaudeCode")
            {
                if (string.IsNullOrWhiteSpace(extra))
                {
                    _logger.LogError("agent.Extra is null or whitespace");
                    return null;
                }

                var ccOptions = JsonUtil.Deserialize<ClaudeCodeAIAgentOptions>(extra);
                if (ccOptions == null)
                {
                    _logger.LogError("agent.Extra Deserialize to options error");
                    return null;
                }

                if (taskId != null)
                {
                    if (resume)
                    {
                        ccOptions = ccOptions with
                        {
                            Resume = taskId.Value.Normalize(),
                            SessionId = null
                        };
                    }
                    else
                    {
                        ccOptions = ccOptions with
                        {
                            Resume = null,
                            SessionId = taskId
                        };
                    }
                }
                ccOptions = ccOptions with
                {
                    ChatHistoryProvider = _chatHistoryProvider,
                };

                return new ClaudeCodeAIAgent(ccOptions, _logger);
            }
        }

        return await CreateDefinitionAgent(agent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentExecSession?> CreateSessionAsync(
        Guid agentId,
        ProjectTask task,
        SettingCommand settings,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        //if(agent.Name == "ClaudeCode")
        //{
        //    var request = JsonUtil.Deserialize<ClaudeCodeSettingRequest>(extraSetting ?? "")
        //        ?? new ClaudeCodeSettingRequest();
        //    request = request with
        //    {
        //        SessionId = task.Id.ToString()
        //    };
        //    var session = await _claudeCodeService.InitializeSessionAsync(request, cancellationToken);
        //    return session;
        //}


        Guid projectId = task.ProjectId;
        var projectExtraSetting = await GetProjectExtraSettingAsync(projectId);
        var mergedExtra = MergeExtraSettings(agent.Extra, projectExtraSetting, settings.SettingContent);
        string taskIdString = task.Id.Normalize();

        var resolvedContextId = TaskUtil.GenContextId();
        var aiAgent = await CreateAiAgentAsync(agent, mergedExtra, task.Id, projectId, resume: settings.Resume, cancellationToken);
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

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentExecSession session,
        AgwUserInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    public async Task<AgentExecutionResult?> ExecuteByNameAsync(
        string agentName,
        Guid? taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null)
    {
        var agent = await _agentRepository.SingleOrDefaultAsync(a => a.Name == agentName);
        if (agent == null)
        {
            return null;
        }

        var chatMsg = new ChatMessage(ChatRole.User, input)
        {
            AuthorName = Constants.DefaultAuthor
        };
        return await ExecuteAsync(taskId, [chatMsg], projectId, contextId, agent);
    }

    public async Task<AgentExecutionResult?> ExecuteAsync(
        Guid agentId,
        Guid taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null)
    {
        var chatMsg = new ChatMessage(ChatRole.User, input)
        {
            AuthorName = Constants.DefaultAuthor
        };

        return await ExecuteAsync(agentId, taskId, [chatMsg], cancellationToken, projectId, contextId);
    }

    public async Task<AgentExecutionResult?> ExecuteAsync(
        Guid agentId,
        Guid taskId,
        List<ChatMessage> input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await ExecuteAsync(taskId, input, projectId, contextId, agent);
    }


    private async Task<AgentExecutionResult?> ExecuteAsync(
     Guid? taskId,
     List<ChatMessage> chatMsg,
     Guid? projectId,
     string? contextId,
     Agent agent)
    {
        projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        var projectExtraSetting = await GetProjectExtraSettingAsync(projectId);
        var mergedExtra = MergeExtraSettings(agent.Extra, projectExtraSetting, null);
        var aiAgent = await CreateAiAgentAsync(agent, mergedExtra);
        if (aiAgent == null)
        {
            throw new Exception("aiAgent not found");
        }

        try
        {
            AgentSession session;
            if (taskId == null)
            {
                taskId = Guid.NewGuid();
                session = await aiAgent.CreateSessionAsync();
            }
            else
            {
                var value = await _cache.GetOrCreateAsync<string>(taskId.Value.Normalize(), _ => ValueTask.FromResult(string.Empty));
                if (string.IsNullOrWhiteSpace(value))
                {
                    session = await aiAgent.CreateSessionAsync();
                }
                else
                {
                    var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
                    session = await aiAgent.DeserializeSessionAsync(serializedThread);
                }
            }
            string taskIdValue = taskId.Value.Normalize();

            _providerSessionState.InitializeSessionState(
                session,
                string.IsNullOrWhiteSpace(contextId) ? taskIdValue : contextId,
                taskIdValue,
                ProjectDefaults.GetDefaultProjectIdentifier(projectId)
                );

            var stream = aiAgent.RunStreamingAsync(chatMsg, session);
            var messages = new List<AgwMessage>();
            await foreach (var update in stream)
            {
                var msg = update.ToAiMessage();
                if (msg != null)
                {
                    messages.Add(msg);
                }
            }

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


    private async Task<AIAgent?> CreateDefinitionAgent(Agent agentDefinition, CancellationToken cancellationToken)
    {
        if (!agentDefinition.ModelProviderId.HasValue)
        {
            return null;
        }

        var modelProvider = await _modelProviderRepository.GetByIdAsync(agentDefinition.ModelProviderId.Value);
        if (modelProvider == null)
        {
            return null;
        }

        var model = await _modelRepository.GetByIdAsync(modelProvider.ModelId);
        var provider = await _providerRepository.Queryable
            .Include(x => x.AuthConfigs)
            .SingleOrDefaultAsync(x => x.Id == modelProvider.ProviderId);
        if (model == null || provider == null)
        {
            return null;
        }

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
            ProviderType.OpenAI => CreateOpenAiAgent(agentDefinition, model, provider, authConfig, tools, skillsProvider),
            ProviderType.Anthropic => CreateAnthropicAgent(agentDefinition, model, provider, authConfig, tools, skillsProvider),
            _ => throw new NotSupportedException($"Provider type '{provider.ProviderType}' is not supported")
        };
    }

    private AIAgent CreateOpenAiAgent(
        Agent agentDefinition,
        LlmModel model,
        Provider provider,
        ProviderAuthConfig authConfig,
        IList<AITool>? tools,
        AIContextProvider? skillsProvider)
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
                Instructions = agentDefinition.SystemPrompt,
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
        AIContextProvider? skillsProvider)
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
                Instructions = agentDefinition.SystemPrompt,
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
            throw new InvalidOperationException($"Environment variable '{envVariableName}' is not set or empty.");
        }

        return apiKeyFromEnv;
    }

    private async Task<IList<AITool>?> CreateAgentTools(Agent agent, CancellationToken cancellationToken)
    {
        var mergedTools = new List<AITool>();
        if (!string.IsNullOrWhiteSpace(agent.Tools))
        {
            try
            {
                var toolNames = JsonSerializer.Deserialize<string[]>(agent.Tools);
                if (toolNames != null && toolNames.Length > 0)
                {
                    var functions = _toolRegistry.CreateAIFunctions(toolNames);
                    if (functions.Count > 0)
                    {
                        mergedTools.AddRange(functions.Cast<AITool>());
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        var mcpTools = await ListToolsByAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        if (mcpTools.Count > 0)
        {
            mergedTools.AddRange(mcpTools.Cast<AITool>());
        }

        return mergedTools.Count > 0 ? mergedTools : null;
    }

    private async Task<IReadOnlyList<McpClientTool>> ListToolsByAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var links = await _agentMcpToolServerRepository.ListAsync(x => x.AgentId == agentId);
        var serverIds = links.Select(x => x.McpToolServerId).Distinct().ToList();
        if (serverIds.Count == 0)
        {
            return [];
        }

        var servers = await _mcpToolServerRepository.ListAsync(x => x.Enabled && serverIds.Contains(x.Id));
        var tools = new List<McpClientTool>();
        foreach (var server in servers)
        {
            try
            {
                var serverTools = await ListToolsAsync(server, cancellationToken).ConfigureAwait(false);
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

    private static async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(
        McpToolServer server,
        CancellationToken cancellationToken = default)
    {
        var transport = CreateTransport(server);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.AsReadOnly();
    }

    private static IClientTransport CreateTransport(McpToolServer server)
    {
        return server.TransportType.ToLowerInvariant() switch
        {
            "stdio" => CreateStdioTransport(server),
            "http" or "sse" => CreateHttpTransport(server),
            _ => throw new NotSupportedException($"Transport type '{server.TransportType}' is not supported")
        };
    }

    private static StdioClientTransport CreateStdioTransport(McpToolServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new InvalidOperationException($"MCP server '{server.Id}' uses stdio transport but has no command configured");
        }

        var options = new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command,
            Arguments = [.. server.Arguments],
        };

        if (server.EnvironmentVariables.Count > 0)
        {
            options.EnvironmentVariables = new Dictionary<string, string?>(
                server.EnvironmentVariables.Select(kvp => new KeyValuePair<string, string?>(kvp.Key, kvp.Value)));
        }

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            options.WorkingDirectory = server.WorkingDirectory;
        }

        return new StdioClientTransport(options);
    }

    private static HttpClientTransport CreateHttpTransport(McpToolServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
        {
            throw new InvalidOperationException($"MCP server '{server.Id}' uses HTTP/SSE transport but has no URL configured");
        }

        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url),
        };

        if (server.Headers is { Count: > 0 })
        {
            options.AdditionalHeaders = new Dictionary<string, string>(server.Headers);
        }

        return new HttpClientTransport(options);
    }


    private async Task<AgentSession> GetOrCreateThreadAsync(
        Agent agent,
        AIAgent aiAgent,
        string taskId,
        CancellationToken cancellationToken)
    {
        if(agent.Type == AgentType.External)
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

 
    private string? MergeExtraSettings(string? agentExtra, string? projectExtraSetting, string? requestExtraSetting)
    {
        JsonObject? merged = null;

        MergeExtraSetting(ref merged, agentExtra, "Agent.Extra");
        MergeExtraSetting(ref merged, projectExtraSetting, "Project.ExtraSetting");
        MergeExtraSetting(ref merged, requestExtraSetting, "SettingCommand.SettingContent");

        return merged?.ToJsonString();
    }

    private void MergeExtraSetting(ref JsonObject? merged, string? rawSetting, string settingName)
    {
        if (string.IsNullOrWhiteSpace(rawSetting))
        {
            return;
        }

        if (!TryParseJsonObject(rawSetting, out var jsonObject))
        {
            _logger.LogWarning("{SettingName} is not a valid JSON object. Skipping it.", settingName);
            return;
        }

        merged ??= new JsonObject();
        foreach (var pair in jsonObject)
        {
            merged[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private Task<string?> GetProjectExtraSettingAsync(Guid? projectId)
    {
        return _projectAppService.GetProjectExtraSettingAsync(projectId);
    }

    private async Task<AIContextProvider?> CreateSkillsProviderAsync(Guid agentId)
    {
        var relations = await _agentSkillRelationRepository.ListAsync(x => x.AgentId == agentId);
        if (relations.Count == 0)
        {
            return null;
        }

        var skillIds = relations.Select(x => x.SkillId).Distinct().ToList();
        var skills = await _skillRepository.ListAsync(x => skillIds.Contains(x.Id));
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

        return new FileAgentSkillsProvider(skillPaths: skillPaths);
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

    private async Task<bool> HasInvalidModelProviderAsync(Guid? modelProviderId)
    {
        if (!modelProviderId.HasValue)
        {
            return false;
        }

        return await _modelProviderRepository.GetByIdAsync(modelProviderId.Value) == null;
    }

    private async Task SyncAgentMcpToolServerRelationsAsync(Guid agentId, IEnumerable<Guid>? mcpToolServerIds)
    {
        var existingLinks = await _agentMcpToolServerRepository.ListAsync(x => x.AgentId == agentId);
        foreach (var link in existingLinks)
        {
            _agentMcpToolServerRepository.Remove(link);
        }

        var requestedIds = _agentDomainService.NormalizeMcpToolServerIds(mcpToolServerIds);
        if (requestedIds.Count == 0)
        {
            return;
        }

        var existingServers = await _mcpToolServerRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var serverId in existingServers.Select(x => x.Id))
        {
            await _agentMcpToolServerRepository.AddAsync(new AgentMcpToolServer
            {
                AgentId = agentId,
                McpToolServerId = serverId
            });
        }
    }

    private async Task SyncAgentSkillRelationsAsync(Guid agentId, IEnumerable<Guid>? skillIds)
    {
        var existingLinks = await _agentSkillRelationRepository.ListAsync(x => x.AgentId == agentId);
        foreach (var link in existingLinks)
        {
            _agentSkillRelationRepository.Remove(link);
        }

        var requestedIds = (skillIds ?? [])
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var existingSkills = await _skillRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var skillId in existingSkills.Select(x => x.Id))
        {
            await _agentSkillRelationRepository.AddAsync(new AgentSkillRelation
            {
                AgentId = agentId,
                SkillId = skillId
            });
        }
    }

    private async Task SyncMcpToolServerAgentRelationsAsync(Guid mcpToolServerId, IEnumerable<Guid>? agentIds)
    {
        var existingLinks = await _agentMcpToolServerRepository.ListAsync(x => x.McpToolServerId == mcpToolServerId);
        foreach (var link in existingLinks)
        {
            _agentMcpToolServerRepository.Remove(link);
        }

        var requestedIds = _mcpToolServerDomainService.NormalizeAgentIds(agentIds);
        if (requestedIds.Count == 0)
        {
            return;
        }

        var existingAgents = await _agentRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var agentId in existingAgents.Select(x => x.Id))
        {
            await _agentMcpToolServerRepository.AddAsync(new AgentMcpToolServer
            {
                AgentId = agentId,
                McpToolServerId = mcpToolServerId
            });
        }
    }

    private static bool TryParseJsonObject(string json, [NotNullWhen(true)] out JsonObject? jsonObject)
    {
        jsonObject = null;

        try
        {
            jsonObject = JsonNode.Parse(json) as JsonObject;
            return jsonObject != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
