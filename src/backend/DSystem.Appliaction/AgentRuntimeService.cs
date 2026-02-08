using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Shared;
using DSystem.Shared.Enums;
using DSystem.Shared.Models;
using DSystem.Domain.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

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
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<ModelProviderApiKey> _apiKeyRepository;
    private readonly IRepository<ModelProvider> _modelProviderRepository;
    private readonly IRepository<LlmModel> _modelRepository;
    private readonly IRepository<Provider> _providerRepository;
    private readonly ToolRegistryService _toolRegistry;
    private readonly HybridCache _cache;


    public AgentRuntimeService(
        IRepository<Agent> agentRepository,
        IRepository<ModelProviderApiKey> apiKeyRepository,
        IRepository<ModelProvider> modelProviderRepository,
        IRepository<LlmModel> modelRepository,
        IRepository<Provider> providerRepository,
        ToolRegistryService toolRegistry,
        HybridCache cache)
    {
        _agentRepository = agentRepository;
        _apiKeyRepository = apiKeyRepository;
        _modelProviderRepository = modelProviderRepository;
        _modelRepository = modelRepository;
        _providerRepository = providerRepository;
        _toolRegistry = toolRegistry;
        _cache = cache;
    }

    public async Task<AIAgent?> CreateAiAgentAsync(Guid agentId, string? systemPrompt = null)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }
        return await CreateAiAgentAsync(agent);
    }

    public async Task<AIAgent?> CreateAiAgentAsync(Agent agent)
    {
        if (agent.Type == AgentType.External)
        {
            if (agent.Name == "ClaudeCode")
            {

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
    /// Executes an agent with streaming response.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        Guid agentId,
        string threadId,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aiAgent = await CreateAiAgentAsync(agentId);
        if (aiAgent == null)
        {
            yield break;
        }

        AgentSession agentSession;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            threadId = Guid.NewGuid().ToString();
            agentSession = await aiAgent.GetNewSessionAsync();
        }
        else
        {
            var value = await _cache.GetOrCreateAsync<string>(threadId, (c) =>
            {
                return ValueTask.FromResult("");
            });
            if (string.IsNullOrWhiteSpace(value))
            {
                agentSession = await aiAgent.GetNewSessionAsync();
            }
            else
            {
                var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
                agentSession = await aiAgent.DeserializeSessionAsync(serializedThread);
            }
        }

        ChatMessage? system = null;
        var chatMsg = new ChatMessage(ChatRole.User, input);
        IEnumerable<ChatMessage> msgs = system == null
            ? [chatMsg]
            : [system, chatMsg];

        var stream = aiAgent.RunStreamingAsync(msgs, agentSession);

        await foreach (var update in stream.ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent text)
                {
                    var contentText = text.Text;
                    var contentObj = new AiMessageContent("text", contentText);
                    var msg = new AiMessage(
                        update.MessageId ?? "",
                        update.AuthorName,
                        update.Role?.Value,
                        [contentObj]
                        );
                    yield return msg;
                }
            }
        }

        // Save thread state to cache after execution
        var serialized = JsonSerializer.Serialize(agentSession.Serialize());
        await _cache.SetAsync(threadId, serialized, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Executes an agent with the given input and returns the result.
    /// </summary>
    public async Task<AgentExecutionResult?> ExecuteAsync(
        Guid agentId,
        string threadId,
        string input,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        var aiAgent = await CreateAiAgentAsync(agent);
        if(aiAgent == null)
        {
            throw new Exception("aiAgent not found");
        }



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
        await foreach (var update in stream)
        {
            if(update != null)
            {
               var msg = update.ToAiMessage();

            }
        }

        return new AgentExecutionResult(
            threadId,
            messages
            );
    }
}

