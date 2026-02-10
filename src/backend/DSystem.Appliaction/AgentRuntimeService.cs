using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Shared;
using DSystem.Shared.Enums;
using DSystem.Shared.Models;
using DSystem.Domain.Repositories;
using DSystem.SessionRecords.Application;
using DSystem.Appliaction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ClaudeCodeSdk.MAF;
using Microsoft.Extensions.Logging;

namespace DSystem.Appliaction.Services;

/// <summary>
/// Result of a single agent execution.
/// </summary>
public record AgentExecutionResult(
    string ThreadId,
    IReadOnlyList<AiMessage> Messages);

/// <summary>
/// Shapes persisted Agent data plus its Model/Provider/API key into a runtime payload
/// consumable by MicrosoftAgentFramework when creating an <see cref="AiAgent"/>.
/// </summary>
public class AgentRuntimeService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<Project> _projectRepository;
    private readonly IRepository<ModelProviderApiKey> _apiKeyRepository;
    private readonly IRepository<ModelProvider> _modelProviderRepository;
    private readonly IRepository<LlmModel> _modelRepository;
    private readonly IRepository<Provider> _providerRepository;
    private readonly ToolRegistryService _toolRegistry;
    private readonly HybridCache _cache;
    private readonly SessionRecordApplication _sessionRecordApplication;


    public AgentRuntimeService(
        IRepository<Agent> agentRepository,
        IRepository<Project> projectRepository,
        IRepository<ModelProviderApiKey> apiKeyRepository,
        IRepository<ModelProvider> modelProviderRepository,
        IRepository<LlmModel> modelRepository,
        IRepository<Provider> providerRepository,
        ToolRegistryService toolRegistry,
        HybridCache cache,
        SessionRecordApplication sessionRecordApplication,
        ILogger<AgentRuntimeService> logger)
    {
        _agentRepository = agentRepository;
        _projectRepository = projectRepository;
        _apiKeyRepository = apiKeyRepository;
        _modelProviderRepository = modelProviderRepository;
        _modelRepository = modelRepository;
        _providerRepository = providerRepository;
        _toolRegistry = toolRegistry;
        _cache = cache;
        _sessionRecordApplication = sessionRecordApplication;
        _logger = logger;
    }

    public async Task<AIAgent?> CreateAiAgentAsync(Guid agentId, string? systemPrompt = null, string? extraOverride = null)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }
        return await CreateAiAgentAsync(agent, extraOverride);
    }

    public async Task<AIAgent?> CreateAiAgentAsync(Agent agent, string? extraOverride = null)
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
                var ccAgent = new ClaudeCodeAIAgent(ccOptions, _logger);
                return ccAgent;
            }
        }
        // External agents may not have a ModelProviderApiKeyId
        if (!agent.ModelProviderApiKeyId.HasValue)
        {
            return null;
        }

        var apiKey = await _apiKeyRepository.GetByIdAsync(agent.ModelProviderApiKeyId.Value);
        if (apiKey == null || !apiKey.Enable)
        {
            return null;
        }

        var modelProvider = await _modelProviderRepository.GetByIdAsync(apiKey.ModelProviderId);
        if (modelProvider == null)
        {
            return null;
        }

        var model = await _modelRepository.GetByIdAsync(modelProvider.ModelId);
        var provider = await _providerRepository.GetByIdAsync(modelProvider.ProviderId);
        if (model == null || provider == null)
        {
            return null;
        }

        // Create tools if specified using the new unified approach
        IList<AITool>? tools = null;
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
                        tools = functions.Cast<AITool>().ToList();
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid JSON, skip tools
                tools = null;
            }
        }

        ApiKeyCredential credential = new ApiKeyCredential(apiKey.ApiKey);
        OpenAIClientOptions options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Endpoint),
        };
        OpenAIClient client = new OpenAIClient(credential, options);
        var chatCompletionClient = client.GetChatClient(model.Name);
        AIAgent aIAgent = chatCompletionClient.AsAIAgent(
            instructions: agent.SystemPrompt,
            name: agent.Name,
            tools: tools
            );

        return aIAgent;
    }

    /// <summary>
    /// Creates an AI agent session for the provided thread.
    /// </summary>
    public async Task<AiAgentSession?> CreateSessionAsync(
        Guid agentId,
        string threadId,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        var projectExtraSetting = await GetProjectExtraSettingAsync(projectId);
        var mergedExtra = MergeExtraSettings(agent.Extra, projectExtraSetting);
        var aiAgent = await CreateAiAgentAsync(agent, mergedExtra);
        if (aiAgent == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            threadId = Guid.NewGuid().ToString();
        }

        var agentSession = await GetOrCreateThreadAsync(aiAgent, threadId, cancellationToken);
        return new AiAgentSession(
            aiAgent,
            agentSession,
            projectId ?? Guid.Empty,
            threadId,
            _logger,
            _sessionRecordApplication);
    }

    /// <summary>
    /// Executes an existing AI agent session with streaming response.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        AiAgentSession session,
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
            await SaveSessionThreadStateAsync(session._sessionId, session.Session, cancellationToken);
        }
    }

    /// <summary>
    /// Executes an agent with streaming response.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        Guid agentId,
        string threadId,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Guid? projectId = null)
    {
        var session = await CreateSessionAsync(agentId, threadId, projectId, cancellationToken);
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
        string threadId,
        CancellationToken cancellationToken)
    {
        var value = await _cache.GetOrCreateAsync<string>(threadId, _ => ValueTask.FromResult(""), cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            return await aiAgent.GetNewSessionAsync();
        }

        try
        {
            var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
            return await aiAgent.DeserializeSessionAsync(serializedThread);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Thread cache deserialization failed for threadId: {ThreadId}. A new thread will be created.", threadId);
            return await aiAgent.GetNewSessionAsync();
        }
    }

    private Task SaveSessionThreadStateAsync(string threadId, AgentSession session, CancellationToken cancellationToken)
    {
        var serialized = JsonSerializer.Serialize(session.Serialize());
        return _cache.SetAsync(threadId, serialized, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Executes an agent with the given input and returns the result.
    /// </summary>
    public async Task<AgentExecutionResult?> ExecuteAsync(
        Guid agentId,
        string threadId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        var projectExtraSetting = await GetProjectExtraSettingAsync(projectId);
        var mergedExtra = MergeExtraSettings(agent.Extra, projectExtraSetting);
        var aiAgent = await CreateAiAgentAsync(agent, mergedExtra);
        if (aiAgent == null)
        {
            throw new Exception("aiAgent not found");
        }
        try
        {

            AgentSession thread;
            if (string.IsNullOrWhiteSpace(threadId))
            {
                threadId = Guid.NewGuid().ToString();
                thread = await aiAgent.GetNewSessionAsync();
            }
            else
            {
                var value = await _cache.GetOrCreateAsync<string>(threadId, (c) =>
                {
                    return ValueTask.FromResult("");
                });
                if (string.IsNullOrWhiteSpace(value))
                {
                    thread = await aiAgent.GetNewSessionAsync();
                }
                else
                {
                    var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
                    thread = await aiAgent.DeserializeSessionAsync(serializedThread);
                }
            }

            ChatMessage? system = null;
            var chatMsg = new ChatMessage(ChatRole.User, input);
            IEnumerable<ChatMessage> msgs = system == null
                ? [chatMsg]
                : [system, chatMsg];
            var stream = aiAgent.RunStreamingAsync(msgs, thread);

            List<AiMessage> messages = new();
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

            await _sessionRecordApplication.SaveThreadStateAsync(
                threadId,
                projectId ?? Guid.Empty,
                thread.Serialize(),
                responseUpdates,
                input,
                cancellationToken);

            return new AgentExecutionResult(
                threadId,
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

    private async Task<string?> GetProjectExtraSettingAsync(Guid? projectId)
    {
        if (!projectId.HasValue || projectId.Value == Guid.Empty)
        {
            return null;
        }

        var project = await _projectRepository.GetByIdAsync(projectId.Value);
        return project?.ExtraSetting;
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
