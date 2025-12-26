using DSystem.Domain.Entities;
using DSystem.Domain.Models;
using DSystem.Domain.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.Text.Json;

namespace DSystem.Domain.Services;

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

    public AgentRuntimeService(
        IRepository<Agent> agentRepository,
        IRepository<ModelProviderApiKey> apiKeyRepository,
        IRepository<ModelProvider> modelProviderRepository,
        IRepository<LlmModel> modelRepository,
        IRepository<Provider> providerRepository,
        ToolRegistryService toolRegistry)
    {
        _agentRepository = agentRepository;
        _apiKeyRepository = apiKeyRepository;
        _modelProviderRepository = modelProviderRepository;
        _modelRepository = modelRepository;
        _providerRepository = providerRepository;
        _toolRegistry = toolRegistry;
    }

    public async Task<AIAgent?> CreateAiAgentAsync(Guid agentId)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null)
        {
            return null;
        }

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
}
