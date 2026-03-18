using Anthropic;
using ClaudeCodeSdk.MAF;
using Agw.Appliaction;
using Agw.Domain.Entities;
using Agw.Domain.Repositories;
using Agw.Domain.Services;
using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Tasks;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Appliaction.Services;

/// <summary>
/// Result of a single agent execution.
/// </summary>
public record AgentExecutionResult(
    string SessionId,
    IReadOnlyList<AgwMessage> Messages);

/// <summary>
/// Shapes persisted Agent data plus its Model/Provider/API key into a runtime payload
/// consumable by MicrosoftAgentFramework when creating an <see cref="AiAgent"/>.
/// </summary>
public class AgentRuntimeService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IProjectAppService _projectAppService;
    private readonly IRepository<ModelProvider> _modelProviderRepository;
    private readonly IRepository<LlmModel> _modelRepository;
    private readonly IRepository<Provider> _providerRepository;
    private readonly ToolRegistryService _toolRegistry;
    private readonly HybridCache _cache;
    private readonly ITaskAppService _taskRecordApplication;
    private readonly McpToolServerDomainService _mcpToolServerDomainService;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;


    public AgentRuntimeService(
        IRepository<Agent> agentRepository,
        IProjectAppService projectAppService,
        IRepository<ModelProvider> modelProviderRepository,
        IRepository<LlmModel> modelRepository,
        IRepository<Provider> providerRepository,
        ToolRegistryService toolRegistry,
        HybridCache cache,
        ITaskAppService taskRecordApplication,
        McpToolServerDomainService mcpToolServerDomainService,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        ILogger<AgentRuntimeService> logger)
    {
        _agentRepository = agentRepository;
        _projectAppService = projectAppService;
        _modelProviderRepository = modelProviderRepository;
        _modelRepository = modelRepository;
        _providerRepository = providerRepository;
        _toolRegistry = toolRegistry;
        _cache = cache;
        _taskRecordApplication = taskRecordApplication;
        _mcpToolServerDomainService = mcpToolServerDomainService;
        _chatHistoryProvider = chatHistoryProvider;
        _providerSessionState = providerSessionState;
        _logger = logger;
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
        string? sessionId = null,
        string? projectId = null,
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

                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    if (await HasSessionRecordAsync(sessionId, projectId))
                    {
                        ccOptions = ccOptions with
                        {
                            Resume = sessionId,
                            SessionId = null
                        };
                    }
                    else
                    {
                        var sessionGuid = Guid.Parse(sessionId);
                        ccOptions = ccOptions with
                        {
                            Resume = null,
                            SessionId = sessionGuid
                        };
                    }
                }

                var ccAgent = new ClaudeCodeAIAgent(ccOptions, _logger);
                return ccAgent;
            }
        }
        return await CreateDefinitionAgent(agent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AIAgent?> CreateDefinitionAgent(Agent agentDefinition, CancellationToken cancellationToken)
    {
        // External agents may not have a ModelProviderId
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
        var provider = await _providerRepository
            .Queryable.Include(x => x.AuthConfigs)
            .SingleOrDefaultAsync(x => x.Id == modelProvider.ProviderId);
        if (model == null || provider == null)
        {
            return null;
        }

        var authConfigs = provider.AuthConfigs
            .Where(x => x.Enable)
            .ToList();
        if (authConfigs.Count == 0)
        {
            _logger.LogError("no auth config for provider:{}", provider.Name);
            return null;
        }
        Random rand = new Random();
        int index = rand.Next(authConfigs.Count);
        var authConfig = authConfigs.ElementAt(index);

        IList<AITool>? tools = await CreateAgentTools(agentDefinition, cancellationToken).ConfigureAwait(false);
        AIAgent? aIAgent = null;
        switch (provider.ProviderType)
        {
            case ProviderType.OpenAI:
                {
                    OpenAIClient client;
                    if (authConfig.AuthType == ProviderAuthType.ApiKey)
                    {
                        var apiKey = authConfig.ApiKey!;
                        var credential = new ApiKeyCredential(apiKey);
                        var options = new OpenAIClientOptions
                        {
                            Endpoint = new Uri(provider.Endpoint),
                        };
                        client = new OpenAIClient(credential, options);
                    }
                    else
                    {
                        var envVariableName = authConfig.EnvName!;
                        var apiKeyFromEnv = Environment.GetEnvironmentVariable(envVariableName);
                        if (string.IsNullOrWhiteSpace(apiKeyFromEnv))
                        {
                            _logger.LogError("Environment variable '{EnvName}' is not set or empty.", envVariableName);
                            return null;
                        }
                        var credential = new ApiKeyCredential(apiKeyFromEnv);
                        var options = new OpenAIClientOptions
                        {
                            Endpoint = new Uri(provider.Endpoint),
                        };
                        client = new OpenAIClient(credential, options);
                    }

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

                    aIAgent = chatCompletionClient.AsAIAgent(
                        agentOptions
                    )
                        .AsBuilder()
                        .UseOpenTelemetry(sourceName: provider.Name, configure: (cfg) =>
                            cfg.EnableSensitiveData = true)
                        .Build();
                    break;
                }
            case ProviderType.Anthropic:
                {
                    AnthropicClient client;
                    if (authConfig.AuthType == ProviderAuthType.ApiKey)
                    {
                        var apiKey = authConfig.ApiKey!;
                        var anthropicClientOptions = new Anthropic.Core.ClientOptions
                        {
                            ApiKey = apiKey,
                            BaseUrl = provider.Endpoint
                        };
                        client = new AnthropicClient(anthropicClientOptions);
                    }
                    else
                    {
                        var envVariableName = authConfig.EnvName!;
                        var apiKeyFromEnv = Environment.GetEnvironmentVariable(envVariableName);
                        if (string.IsNullOrWhiteSpace(apiKeyFromEnv))
                        {
                            _logger.LogError("Environment variable '{EnvName}' is not set or empty.", envVariableName);
                            return null;
                        }
                        var anthropicClientOptions = new Anthropic.Core.ClientOptions
                        {
                            ApiKey = apiKeyFromEnv,
                            BaseUrl = provider.Endpoint
                        };
                        client = new AnthropicClient(anthropicClientOptions);
                    }

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

                    aIAgent = client.AsAIAgent(
                        agentOptions
                        )
                        .AsBuilder()
                        .UseOpenTelemetry(sourceName: provider.Name, configure: (cfg) =>
                            cfg.EnableSensitiveData = true)
                        .Build();
                    break;
                }
            default: throw new NotSupportedException($"Provider type '{provider.ProviderType}' is not supported");
        }

        return aIAgent;
    }

    private async Task<IList<AITool>?> CreateAgentTools(Agent agent, CancellationToken cancellationToken)
    {
        // Create tools if specified using the new unified approach
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
                // Invalid JSON, skip tools
            }
        }

        // mcp tools
        var mcpTools = await _mcpToolServerDomainService
                .ListToolsByAgentAsync(agent.Id, cancellationToken)
                .ConfigureAwait(false);
        if (mcpTools.Count > 0)
        {
            mergedTools.AddRange(mcpTools.Cast<AITool>());
        }
        IList<AITool>? tools = mergedTools.Count > 0 ? mergedTools : null;
        return tools;
    }



    /// <summary>
    /// Creates an AI agent session for the provided thread.
    /// </summary>
    public async Task<AgentExecSession?> CreateSessionAsync(
        Guid agentId,
        string sessionId,
        string? projectId = null,
        string? contextId = null,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        var projectExtraSetting = await GetProjectExtraSettingAsync(projectId);
        var mergedExtra = MergeExtraSettings(agent.Extra, projectExtraSetting);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString();
        }

        var resolvedContextId = string.IsNullOrWhiteSpace(contextId) ? sessionId : contextId;
        var aiAgent = await CreateAiAgentAsync(agent, mergedExtra, sessionId, projectId, cancellationToken);
        if (aiAgent == null)
        {
            return null;
        }
        var agentSession = await GetOrCreateThreadAsync(aiAgent, sessionId, cancellationToken);
        _providerSessionState.InitializeSessionState(
            agentSession,
            resolvedContextId,
            sessionId,
            projectId);
        return new AgentExecSession(
            aiAgent,
            agentSession,
            projectId ?? string.Empty,
            resolvedContextId,
            sessionId,
            ProjectTaskAgentType.Agent,
            agentId,
            agent.Name,
            _logger,
            taskTitle: agent.Name);
    }

    private async Task<bool> HasSessionRecordAsync(string sessionId, string? projectId)
    {
        return await _taskRecordApplication.HasSessionAsync(sessionId, projectId);
    }

    /// <summary>
    /// Executes an existing AI agent session with streaming response.
    /// </summary>
    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentExecSession session,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            await foreach (var message in session.ExecuteStreamingAsync(input, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            await SaveSessionThreadStateAsync(session._sessionId, session.Agent, session.Session, cancellationToken);
        }
    }

    /// <summary>
    /// Executes an agent with streaming response.
    /// </summary>
    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentId,
        string sessionId,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? projectId = null,
        string? contextId = null)
    {
        var session = await CreateSessionAsync(agentId, sessionId, projectId, contextId, cancellationToken);
        if (session == null)
        {
            yield break;
        }

        await using (session)
        {
            await foreach (var message in ExecuteStreamingAsync(session, input, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
    }

    private async Task<AgentSession> GetOrCreateThreadAsync(
        AIAgent aiAgent,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var value = await _cache.GetOrCreateAsync<string>(sessionId, _ => ValueTask.FromResult(""), cancellationToken: cancellationToken);
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
            _logger.LogWarning("Thread cache deserialization failed for sessionId: {SessionId}. A new thread will be created.", sessionId);
            return await aiAgent.CreateSessionAsync();
        }
    }

    private async Task SaveSessionThreadStateAsync(string sessionId, AIAgent aiAgent, AgentSession session, CancellationToken cancellationToken)
    {
        var ele = await aiAgent.SerializeSessionAsync(session);
        var serialized = JsonSerializer.Serialize(ele);
        await _cache.SetAsync(sessionId, serialized, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Executes an agent with the given agentName, input and returns the result.
    /// </summary>
    public async Task<AgentExecutionResult?> ExecuteByNameAsync(
        string agentName,
        string sessionId,
        string input,
        CancellationToken cancellationToken = default,
        string? projectId = null,
        string? contextId = null
        )
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
        return await ExecuteAsync(sessionId, [chatMsg], projectId, contextId, agent);
    }


    /// <summary>
    /// Executes an agent with the given agentId, input and returns the result.
    /// </summary>
    public async Task<AgentExecutionResult?> ExecuteAsync(
        Guid agentId,
        string sessionId,
        string input,
        CancellationToken cancellationToken = default,
        string? projectId = null,
        string? contextId = null)
    {
        var chatMsg = new ChatMessage(ChatRole.User, input)
        {
            AuthorName = Constants.DefaultAuthor
        };

        return await ExecuteAsync(agentId, sessionId, [chatMsg], cancellationToken, projectId, contextId);
    }


    /// <summary>
    /// Executes an agent with the given agentId, input and returns the result.
    /// </summary>
    public async Task<AgentExecutionResult?> ExecuteAsync(
        Guid agentId,
        string sessionId,
        List<ChatMessage> input,
        CancellationToken cancellationToken = default,
        string? projectId = null,
        string? contextId = null)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await ExecuteAsync(sessionId, input, projectId, contextId, agent);
    }

    private async Task<AgentExecutionResult?> ExecuteAsync(
        string sessionId,
        List<ChatMessage> chatMsg,
        string? projectId,
        string? contextId,
        Agent agent)
    {
        var projectExtraSetting = await GetProjectExtraSettingAsync(projectId);
        var mergedExtra = MergeExtraSettings(agent.Extra, projectExtraSetting);
        var aiAgent = await CreateAiAgentAsync(agent, mergedExtra);
        if (aiAgent == null)
        {
            throw new Exception("aiAgent not found");
        }
        try
        {

            AgentSession session;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                sessionId = Guid.NewGuid().ToString();
                session = await aiAgent.CreateSessionAsync();
            }
            else
            {
                var value = await _cache.GetOrCreateAsync<string>(sessionId, (c) =>
                {
                    return ValueTask.FromResult("");
                });
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

            _providerSessionState.InitializeSessionState(
                session,
                string.IsNullOrWhiteSpace(contextId) ? sessionId : contextId,
                sessionId,
                projectId);
            // in CreateAiAgentAsync, ChatOptions.Instructions is setted agent.SystemPrompt
            // if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
            // {
            //     ChatMessage system = new ChatMessage(ChatRole.System, agent.SystemPrompt);
            //    chatMsg.Insert(0, system);
            // }

            var stream = aiAgent.RunStreamingAsync(chatMsg, session);

            List<AgwMessage> messages = new();
            var responseUpdates = new List<AgentResponseUpdate>();
            await foreach (var update in stream)
            {
                responseUpdates.Add(update);
                var msg = update.ToAiMessage();
                if (msg != null)
                {
                    messages.Add(msg);
                }
            }

            //await _taskRecordApplication.SaveThreadStateAsync(
            //    sessionId,
            //    string.IsNullOrWhiteSpace(contextId) ? sessionId : contextId,
            //    projectId ?? string.Empty,
            //    ProjectTaskAgentType.Agent,
            //    agent.Id,
            //    agent.Name,
            //    responseUpdates,
            //    input,
            //    cancellationToken: CancellationToken.None);

            return new AgentExecutionResult(
                sessionId,
                messages
                );
        }
        finally
        {
            if (aiAgent is IAsyncDisposable asyncDisable)
            {
                await asyncDisable.DisposeAsync();
            }
            else if (aiAgent is IDisposable disable)
            {
                disable.Dispose();
            }
        }
    }

    private string? MergeExtraSettings(string? agentExtra, string? projectExtraSetting)
    {
        if (string.IsNullOrWhiteSpace(projectExtraSetting))
        {
            return agentExtra;
        }

        if (string.IsNullOrWhiteSpace(agentExtra))
        {
            return projectExtraSetting;
        }

        if (!TryParseJsonObject(agentExtra, out var merged))
        {
            _logger.LogWarning("Agent.Extra is not a valid JSON object. Using Project.ExtraSetting instead.");
            return projectExtraSetting;
        }

        if (!TryParseJsonObject(projectExtraSetting, out var projectExtra))
        {
            _logger.LogWarning("Project.ExtraSetting is not a valid JSON object. Keeping Agent.Extra.");
            return agentExtra;
        }

        foreach (var pair in projectExtra)
        {
            merged[pair.Key] = pair.Value?.DeepClone();
        }

        return merged.ToJsonString();
    }

    private Task<string?> GetProjectExtraSettingAsync(string? projectId)
    {
        return _projectAppService.GetProjectExtraSettingAsync(projectId);
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
