using DSystem.Domain.Entities;
using DSystem.Domain.Models;
using DSystem.Domain.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using OpenAI;
 using OpenAI.Chat;
using System.ClientModel;
using System.Reflection;
using System.Text.Json;
using AIMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenAIMessage = OpenAI.Chat.ChatMessage;

namespace DSystem.Domain.Services;

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
        var apiKey = await _apiKeyRepository.GetByIdAsync(agent.ModelProviderApiKeyId);
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

        // Create tools if specified
        IList<AITool>? tools = null;
        if (!string.IsNullOrWhiteSpace(agent.Tools))
        {
            try
            {
                var toolNames = JsonSerializer.Deserialize<string[]>(agent.Tools);
                if (toolNames != null && toolNames.Length > 0)
                {
                    tools = new List<AITool>();
                    foreach (var toolName in toolNames)
                    {
                        var methodInfo = _toolRegistry.GetToolMethod(toolName);
                        if (methodInfo != null)
                        {
                            // Create delegate from static method
                            var delegateType = GetDelegateType(methodInfo);
                            var toolDelegate = Delegate.CreateDelegate(delegateType, methodInfo);
                            var aiTool = AIFunctionFactory.Create(toolDelegate);
                            tools.Add(aiTool);
                        }
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
        AIAgent aIAgent = chatCompletionClient.CreateAIAgent(
            instructions: agent.Instructions,
            name: agent.Name,
            tools: tools
            );

        return aIAgent;
    }

    /// <summary>
    /// Gets the appropriate delegate type for a method.
    /// </summary>
    private static Type GetDelegateType(System.Reflection.MethodInfo method)
    {
        var parameters = method.GetParameters();
        var paramTypes = parameters.Select(p => p.ParameterType).ToArray();
        var returnType = method.ReturnType;

        // Build Func<> or Action<> type
        if (returnType == typeof(void))
        {
            return paramTypes.Length switch
            {
                0 => typeof(Action),
                1 => typeof(Action<>).MakeGenericType(paramTypes),
                2 => typeof(Action<,>).MakeGenericType(paramTypes),
                3 => typeof(Action<,,>).MakeGenericType(paramTypes),
                4 => typeof(Action<,,,>).MakeGenericType(paramTypes),
                _ => throw new NotSupportedException($"Action with {paramTypes.Length} parameters is not supported")
            };
        }
        else
        {
            var allTypes = paramTypes.Concat(new[] { returnType }).ToArray();
            return allTypes.Length switch
            {
                1 => typeof(Func<>).MakeGenericType(allTypes),
                2 => typeof(Func<,>).MakeGenericType(allTypes),
                3 => typeof(Func<,,>).MakeGenericType(allTypes),
                4 => typeof(Func<,,,>).MakeGenericType(allTypes),
                5 => typeof(Func<,,,,>).MakeGenericType(allTypes),
                _ => throw new NotSupportedException($"Func with {allTypes.Length - 1} parameters is not supported")
            };
        }
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

        AgentThread thread;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            threadId = Guid.NewGuid().ToString();
            thread = aiAgent.GetNewThread();
        }
        else
        {
            var value = await _cache.GetOrCreateAsync<string>(threadId, (c) =>
            {
                return ValueTask.FromResult("");
            });
            if (string.IsNullOrWhiteSpace(value))
            {
                thread = aiAgent.GetNewThread();
            }
            else
            {
                var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
                thread = aiAgent.DeserializeThread(serializedThread);
            }
        }

        ChatMessage? system = null;
        if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
        {
            system = new ChatMessage(ChatRole.System, agent.SystemPrompt);
        }
        var chatMsg = new ChatMessage(ChatRole.User, input);
        IEnumerable<ChatMessage> msgs = system == null
            ? [chatMsg]
            : [system, chatMsg];
        var stream = aiAgent.RunStreamingAsync(msgs, thread);

        var resultMessages = new List<AiMessage>();
        await foreach (var update in stream)
        {
            foreach(var content in update.Contents)
            {
                if(content is TextContent text)
                {
                    var contentText = text.Text;
                    var msg = new AiMessage(update.MessageId, update.AuthorName, update.Role?.Value, contentText);
                    resultMessages.Add(msg);
                }
            }

        }

        return new AgentExecutionResult(
            threadId,
            resultMessages);
    }
}
