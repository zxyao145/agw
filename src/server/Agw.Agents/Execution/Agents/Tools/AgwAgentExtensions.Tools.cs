using Agw.Agents.Execution.Agents.AIContextProviders.PlanMode;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Utils;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Tools;

public static class AgwAgentExtensions
{
    public static AIAgent AsAgwAgent(
        this IChatClient chatClient,
        ResolvedAgentDefinition definition,
        AgentCapabilityComposition capabilities,
        ILoggerFactory loggerFactory,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(services);

        var chatOptions = new ChatOptions
        {
            ModelId = definition.ModelId,
            Instructions = AgentRuntimeServiceUtil.BuildInstructions(definition.SystemPrompt),
            Tools = capabilities.Tools.Count == 0 ? null : capabilities.Tools.ToList(),
            MaxOutputTokens = definition.MaxOutputTokens
        };
        var modeProvider = capabilities.ContextProviders.OfType<AgentModeProvider>().FirstOrDefault();
        var contextProviders = capabilities.ContextProviders.ToList();
        if (definition.CompactionProvider != null)
        {
            contextProviders.Add(definition.CompactionProvider);
        }

        if (modeProvider != null)
        {
            contextProviders.Add(new PlanModeToolGuardProvider(
                modeProvider,
                capabilities.PlanModeAllowedToolNames,
                loggerFactory.CreateLogger<PlanModeToolGuardProvider>()));
        }

        var chatClientBuilder = chatClient.AsBuilder()
            .UseApprovalResponseBinding(loggerFactory)
            .UseApprovalNotRequiredFunctionBypassing()
            .UseFunctionInvocation(loggerFactory);
        if (modeProvider != null)
        {
            chatClientBuilder.Use(static innerClient =>
                new PlanModeToolVisibilityChatClient(innerClient));
        }

        chatClientBuilder
            .UseMessageInjection()
            .Use(static innerClient => new FunctionResultOrderingChatClient(innerClient))
            .UsePerServiceCallChatHistoryPersistence()
            .UseOpenTelemetry(
                sourceName: definition.OpenTelemetrySourceName,
                configure: static options => options.EnableSensitiveData = true);

        var configuredChatClient = chatClientBuilder.Build(services);
        capabilities.AddResource(configuredChatClient);
        var innerAgent = configuredChatClient.AsAIAgent(
            new ChatClientAgentOptions
            {
                Id = definition.Id,
                Name = definition.Name,
                Description = definition.Description,
                ChatOptions = chatOptions,
                ChatHistoryProvider = definition.ChatHistoryProvider,
                AIContextProviders = contextProviders,
                UseProvidedChatClientAsIs = true,
                RequirePerServiceCallChatHistoryPersistence = true,
                WarnOnChatHistoryProviderConflict = false,
                ThrowOnChatHistoryProviderConflict = false
            },
            loggerFactory,
            services);

        var agentBuilder = innerAgent.AsBuilder();
        if (capabilities.LoopEvaluators.Count > 0)
        {
            agentBuilder.Use((inner, _) => new LoopAgent(
                inner,
                capabilities.LoopEvaluators,
                new LoopAgentOptions { MaxIterations = 10 },
                loggerFactory));
        }

        var todoProvider = capabilities.ContextProviders.OfType<TodoProvider>().FirstOrDefault();
        if (todoProvider != null)
        {
            var todoStateSnapshotMiddleware = new TodoStateSnapshotMiddleware(todoProvider);
            agentBuilder.Use(
                runFunc: null,
                runStreamingFunc: todoStateSnapshotMiddleware.RunStreamingAsync);
        }

        if (modeProvider != null)
        {
            var modeStateSnapshotMiddleware = new ModeStateSnapshotMiddleware(modeProvider);
            agentBuilder.Use(
                runFunc: modeStateSnapshotMiddleware.RunAsync,
                runStreamingFunc: modeStateSnapshotMiddleware.RunStreamingAsync);
        }

        if (capabilities.ToolWarnings.Count > 0)
        {
            var warningMiddleware = new ToolWarningMiddleware(
                capabilities.ToolWarnings);
            agentBuilder.Use(
                runFunc: warningMiddleware.RunAsync,
                runStreamingFunc: warningMiddleware.RunStreamingAsync);
        }

        if (capabilities.ToolInvocationWarnings.Count > 0)
        {
            var invocationWarningMiddleware = new ToolInvocationWarningMiddleware(
                capabilities.ToolInvocationWarnings);
            agentBuilder.Use(
                runFunc: invocationWarningMiddleware.RunAsync,
                runStreamingFunc: invocationWarningMiddleware.RunStreamingAsync);
        }

        agentBuilder.UseToolApproval(new ToolApprovalAgentOptions
        {
            AutoApprovalRules = capabilities.AutoApprovalRules
        });
        agentBuilder.UseOpenTelemetry(sourceName: definition.OpenTelemetrySourceName);
        return agentBuilder.Build(services);
    }
}
