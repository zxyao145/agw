using System.ClientModel;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Contracts;
using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware.Approval;
using Agw.Agents.Execution.Agents.Middleware.ModelInput;
using Agw.Agents.Execution.Agents.Middleware.Telemetry;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Context;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.References;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Tools.Impl.ToolBlocks.UserMemory;
using Anthropic;
using Anthropic.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Agw.Agents.Execution.Agents.Runtime;

public partial class AgentRuntimeFactory
{
    private async Task<AIAgent?> CreateDefinitionAgentAsync(
        Agent agentDefinition,
        Project project,
        Guid conversationId,
        IReadOnlyDictionary<string, string> environmentVariables,
        string defaultMode,
        CancellationToken cancellationToken,
        int backgroundDepth = 0,
        bool deferHumanInteractions = false
    )
    {
        if (!agentDefinition.ModelProviderId.HasValue)
        {
            return null;
        }

        var runtimeConfiguration = await _agentAppService.GetModelRuntimeConfigurationAsync(
            agentDefinition.ModelProviderId.Value
        );
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
        var supportsHostedWebSearch = provider.ProviderType == ProviderType.OpenAIResponses;
        Func<IReadOnlyList<Guid>, CancellationToken, ValueTask<IReadOnlyList<AIAgent>>>? backgroundAgentFactory =
            backgroundDepth == 0
                ? (ids, token) =>
                    CreateBackgroundAgentsAsync(
                        ids,
                        agentDefinition.Id,
                        project,
                        conversationId,
                        environmentVariables,
                        defaultMode,
                        token
                    )
                : null;
        var capabilities = await _capabilityComposer
            .ComposeAsync(
                agentDefinition,
                project,
                environmentVariables,
                cancellationToken,
                supportsHostedWebSearch,
                defaultMode,
                backgroundAgentFactory,
                conversationId,
                deferHumanInteractions
            )
            .ConfigureAwait(false);
        AIAgent? aiAgent = null;
        try
        {
            var skillsProvider = await CreateSkillsProviderAsync(
                    agentDefinition,
                    project,
                    capabilities.PluginSkills,
                    capabilities
                )
                .ConfigureAwait(false);
            if (skillsProvider != null)
            {
                capabilities.AddResource(skillsProvider);
                capabilities.AddContextProvider(skillsProvider);
                capabilities.AddAutoApprovalRule(AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule);
                capabilities.AddPlanModeAllowedToolNames([
                    AgentSkillsProvider.LoadSkillToolName,
                    AgentSkillsProvider.ReadSkillResourceToolName,
                ]);
            }

            var chatClient = provider.ProviderType switch
            {
                ProviderType.OpenAIChatCompletions => CreateOpenAiChatClient(model, provider, authConfig),
                ProviderType.OpenAIResponses => CreateOpenAiResponsesChatClient(model, provider, authConfig),
                ProviderType.Anthropic => CreateAnthropicChatClient(model, provider, authConfig),
                _ => throw new AgwException(
                    ErrorCodes.UnsupportedProviderType,
                    $"Provider type '{provider.ProviderType}' is not supported"
                ),
            };

            _logger.LogInformation("Creating definition agent {AgentName}", agentDefinition.Name);

            var responseFormat = AgentResponseSchemaFormat.Create(agentDefinition, provider.ProviderType);
            var historyProvider = CreateHistoryProvider(EngineKind.Maf, structuredResult: responseFormat != null);
            aiAgent = chatClient.AsAgwAgent(
                new ResolvedAgentDefinition
                {
                    Id = agentDefinition.Id.ToString("N"),
                    Name = agentDefinition.Name,
                    Description = agentDefinition.Description,
                    SystemPrompt = agentDefinition.SystemPrompt,
                    ModelId = model.Name,
                    OpenTelemetrySourceName = provider.Name,
                    ChatHistoryProvider = historyProvider,
                    CompactionProvider = new CompactionProvider(
                        ContextWindowCompactionPipeline.Create(model.MaxContextWindowTokens, model.MaxOutputTokens),
                        stateKey: $"agw.compaction.{agentDefinition.Id:N}",
                        loggerFactory: _loggerFactory
                    ),
                    MaxOutputTokens = model.MaxOutputTokens,
                    ResponseFormat = responseFormat,
                },
                capabilities,
                _loggerFactory,
                _services
            );
            var userMemoryProvider = capabilities
                .ContextProviders.Select(static provider => provider.GetService<UserMemoryProvider>())
                .SingleOrDefault(static provider => provider != null);
            Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync =
                userMemoryProvider == null ? null : userMemoryProvider.CreateContextMessageAsync;
            aiAgent = new HistoryRecordingAgent(aiAgent, historyProvider, createMemoryContextAsync, _logger);
            var agentBuilder = aiAgent
                .AsBuilder()
                .Use(runFunc: _telemetryMiddleware.RunAsync, runStreamingFunc: _telemetryMiddleware.RunStreamingAsync);
            if (backgroundDepth > 0)
            {
                var approvalMiddleware = new BackgroundAgentApprovalMiddleware();
                agentBuilder.Use(
                    runFunc: approvalMiddleware.RejectNewApprovalAsync,
                    runStreamingFunc: approvalMiddleware.RejectNewApprovalStreamingAsync
                );
            }

            aiAgent = agentBuilder.Build();
            if (responseFormat != null)
            {
                aiAgent = new AgentResponseSchemaExecutionAgent(
                    aiAgent,
                    "system",
                    "none",
                    provider.ProviderType.ToString()
                );
            }

            return new ResourceOwningAIAgent(aiAgent, capabilities);
        }
        catch
        {
            if (aiAgent != null)
            {
                await DisposeAgentWithoutThrowingAsync(aiAgent).ConfigureAwait(false);
            }

            await DisposeResourceWithoutThrowingAsync(capabilities).ConfigureAwait(false);
            throw;
        }
    }

    private IChatClient CreateOpenAiChatClient(
        ModelProviderModelSnapshot model,
        ModelProviderProviderSnapshot provider,
        ProviderAuthConfigSnapshot authConfig
    )
    {
        var apiKey = ResolveApiKey(authConfig);
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions { Endpoint = new Uri(provider.Endpoint) };
        var client = new OpenAIClient(credential, options);
        var chatCompletionClient = client.GetChatClient(model.Name);
        return OpenAiReasoningChatClient.Create(chatCompletionClient, options.Endpoint);
    }

    private IChatClient CreateAnthropicChatClient(
        ModelProviderModelSnapshot model,
        ModelProviderProviderSnapshot provider,
        ProviderAuthConfigSnapshot authConfig
    )
    {
        var anthropicClientOptions = new ClientOptions
        {
            ApiKey = ResolveApiKey(authConfig),
            BaseUrl = provider.Endpoint,
        };
        var client = new AnthropicClient(anthropicClientOptions);
        return AnthropicReasoningChatClient.Create(client.AsIChatClient(model.Name), new Uri(provider.Endpoint));
    }

    private IChatClient CreateOpenAiResponsesChatClient(
        ModelProviderModelSnapshot model,
        ModelProviderProviderSnapshot provider,
        ProviderAuthConfigSnapshot authConfig
    )
    {
        var apiKey = ResolveApiKey(authConfig);
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions { Endpoint = new Uri(provider.Endpoint) };
        var client = new OpenAIClient(credential, options);
#pragma warning disable OPENAI001
        return OpenAiResponsesReasoningChatClient.Create(
            client.GetResponsesClient().AsIChatClient(model.Name),
            options.Endpoint
        );
#pragma warning restore OPENAI001
    }

    private string ResolveApiKey(ProviderAuthConfigSnapshot authConfig)
    {
        return authConfig.ApiKey!;
    }

    private async ValueTask<IReadOnlyList<AIAgent>> CreateBackgroundAgentsAsync(
        IReadOnlyList<Guid> agentIds,
        Guid parentAgentId,
        Project project,
        Guid conversationId,
        IReadOnlyDictionary<string, string> environmentVariables,
        string defaultMode,
        CancellationToken cancellationToken
    )
    {
        var agents = new List<AIAgent>();
        try
        {
            foreach (var agentId in agentIds.Where(id => id != parentAgentId).Distinct())
            {
                var definition = await _agentAppService.GetAgentForCurrentUserAsync(agentId);
                if (definition == null)
                {
                    continue;
                }

                AIAgent? agent;
                if (definition.Type == AgentType.System)
                {
                    agent = await CreateDefinitionAgentAsync(
                            definition,
                            project,
                            conversationId,
                            environmentVariables,
                            defaultMode,
                            cancellationToken,
                            backgroundDepth: 1
                        )
                        .ConfigureAwait(false);
                }
                else if (definition.Type == AgentType.External)
                {
                    var request = new CreateAiAgentRequest
                    {
                        Agent = definition,
                        ProjectId = project.Id,
                        ConversationId = conversationId,
                        EnvironmentVariables = environmentVariables,
                        IsResume = false,
                    };
                    agent = await CreateExternalAgentAsync(
                            request,
                            project,
                            environmentVariables,
                            cancellationToken,
                            isBackground: true
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    continue;
                }

                if (agent != null)
                {
                    // 作用域层不释放内层 Agent，释放职责交给外层的资源持有包装。
                    // The scope layer does not dispose the inner Agent; the outer resource wrapper owns disposal.
                    agents.Add(
                        new ResourceOwningAIAgent(
                            new BackgroundAgentScopeAgent(
                                agent,
                                definition.Id,
                                EngineKinds.Resolve(definition.Type, definition.ExternalAgentKind)
                            ),
                            (IAsyncDisposable)agent
                        )
                    );
                }
            }

            return agents;
        }
        catch
        {
            foreach (var agent in agents)
            {
                await DisposeAgentWithoutThrowingAsync(agent).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async ValueTask DisposeAgentWithoutThrowingAsync(AIAgent agent)
    {
        try
        {
            switch (agent)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch { }
    }

    private static async ValueTask DisposeResourceWithoutThrowingAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
    }
}
