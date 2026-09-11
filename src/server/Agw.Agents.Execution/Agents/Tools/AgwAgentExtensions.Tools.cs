using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Context.PlanMode;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Tools.HumanInteraction;
using Agw.Tools.Impl.ToolBlocks.Todo;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Tools;

public static class AgwAgentExtensions
{
    /// <summary>将 ChatClient 与准备好的 Tool、Provider 等能力组合为可运行的 Agw Agent。</summary>
    /// <remarks>
    /// 先创建 Agent 配置，再配置 ChatClient Pipeline 和 Agent Pipeline。
    /// ChatClient Pipeline 处理 Model 请求和 Tool 调用；Agent Pipeline 处理自动继续执行、状态通知及审批。
    /// Tool 是否需要审批已在创建时声明；这里使用 MAF（Agent SDK）处理审批和批准后的继续执行。
    /// 此方法不发送 Model 请求。创建的 ChatClient 由 capabilities 负责释放。
    /// </remarks>
    public static AIAgent AsAgwAgent(
        this IChatClient chatClient,
        ResolvedAgentDefinition definition,
        AgentCapabilityComposition capabilities,
        ILoggerFactory loggerFactory,
        IServiceProvider services
    )
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(services);

        // 查找管理 Plan / Execute 状态的 Provider；GetService 也能找到被其他 Provider 包装的实例。
        var modeProvider = capabilities
            .ContextProviders.Select(static provider => provider.GetService<AgentModeProvider>())
            .FirstOrDefault(static provider => provider != null);
        var agentOptions = CreateAgentOptions(definition, capabilities, modeProvider, loggerFactory);

        // 启用 Compaction（缩短发送给 Model 的历史内容）时，用 tracker 记录本次 Run 已加入的 Context，避免重复添加。
        var functionLoopContextTracker =
            definition.CompactionProvider == null ? null : new FunctionLoopContextTracker();
        var configuredChatClient = BuildChatClient(
            chatClient,
            definition,
            modeProvider != null,
            functionLoopContextTracker,
            loggerFactory,
            services
        );
        capabilities.AddResource(configuredChatClient);
        var innerAgent = configuredChatClient.AsAIAgent(agentOptions, loggerFactory, services);

        // 配置 Agent Pipeline，并让整个 Run 共用 tracker；下一次 Run 使用独立的记录。
        var agent = BuildAgentPipeline(innerAgent, definition, capabilities, modeProvider, loggerFactory, services);
        return functionLoopContextTracker == null
            ? agent
            : new FunctionLoopContextScopeAgent(agent, functionLoopContextTracker);
    }

    /// <summary>
    /// 创建 ChatClientAgentOptions，并将 Plan 检查安排在 Tool Provider 之后。
    /// 运行时先由 Provider 生成 Tool，再检查哪些 Tool 允许在 Plan 模式使用。
    /// </summary>
    private static ChatClientAgentOptions CreateAgentOptions(
        ResolvedAgentDefinition definition,
        AgentCapabilityComposition capabilities,
        AgentModeProvider? modeProvider,
        ILoggerFactory loggerFactory
    )
    {
        var chatOptions = new ChatOptions
        {
            ModelId = definition.ModelId,
            Instructions = AgentRuntimeServiceUtil.BuildInstructions(definition.SystemPrompt),
            Tools = capabilities.Tools.Count == 0 ? null : capabilities.Tools.ToList(),
            MaxOutputTokens = definition.MaxOutputTokens,
        };
        // 等所有 Provider 生成 Tool 后再添加 Plan 限制；受限 Tool 会对 Model 隐藏，调用时也会被拒绝。
        var contextProviders = capabilities.ContextProviders.ToList();
        if (modeProvider != null)
        {
            contextProviders.Add(
                new PlanModeToolGuardProvider(
                    modeProvider,
                    capabilities.PlanModeAllowedToolNames,
                    loggerFactory.CreateLogger<PlanModeToolGuardProvider>()
                )
            );
        }

        return new ChatClientAgentOptions
        {
            // Agent 的标识和展示信息。
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            // 设置 Model 参数、历史读写方式，以及运行时提供 Context 和 Tool 的 Provider。
            ChatOptions = chatOptions,
            ChatHistoryProvider = definition.ChatHistoryProvider,
            AIContextProviders = contextProviders,
            // 使用 BuildChatClient 配置好的 Pipeline，避免 SDK 再次添加默认 Middleware。
            UseProvidedChatClientAsIs = true,
            // 每次 Model 请求后保存历史，保留 Run 的中间进度；需要配合下方的 UsePerServiceCallChatHistoryPersistence。
            RequirePerServiceCallChatHistoryPersistence = true,
            // 如果 Model 服务和本地 Provider 同时管理历史，记录警告但继续执行。
            WarnOnChatHistoryProviderConflict = true,
            ThrowOnChatHistoryProviderConflict = false,
        };
    }

    /// <summary>配置 ChatClient Pipeline，处理 Model 请求、Tool 调用、审批和历史消息。</summary>
    private static IChatClient BuildChatClient(
        IChatClient chatClient,
        ResolvedAgentDefinition definition,
        bool hasModeProvider,
        FunctionLoopContextTracker? functionLoopContextTracker,
        ILoggerFactory loggerFactory,
        IServiceProvider services
    )
    {
        // 请求按 Use 的注册顺序进入各层，响应按相反顺序返回。
        var toolInvocationExceptionHandler = new ToolInvocationExceptionHandler(
            loggerFactory.CreateLogger<ToolInvocationExceptionHandler>()
        );
        var chatClientBuilder = chatClient
            .AsBuilder()
            // 将用户批准绑定到原始 Tool 调用，防止继续执行时换成其他 Tool 或参数。
            .UseApprovalResponseBinding(loggerFactory)
            // SDK 可能要求同一批 Tool 一起审批；这里让原本无需审批的 Tool 自动继续执行。
            .UseApprovalNotRequiredFunctionBypassing()
            // 将需要用户回答的问题标记为 UserInput，确保 FullAccess 下也会等待用户回答。
            .Use(inner => new HumanInteractionDescriptionChatClient(
                inner,
                services.GetService(typeof(IHumanInteractionContextAccessor)) as IHumanInteractionContextAccessor
            ))
            // 执行 Model 请求的 Tool，并将结果交回 Model；需要审批时先返回审批请求。
            // FunctionInvoker 将 Tool 异常转换为可交给 Model 的错误信息。
            .UseFunctionInvocation(
                loggerFactory,
                functionInvokingClient =>
                    functionInvokingClient.FunctionInvoker = toolInvocationExceptionHandler.InvokeAsync
            );
        // 复制 Function Loop 使用的消息，避免历史处理修改原始内容；批准后继续执行时，跳过已添加的 Context。
        if (functionLoopContextTracker != null)
        {
            chatClientBuilder.Use(innerClient => new FunctionLoopMessageIsolationChatClient(
                innerClient,
                functionLoopContextTracker
            ));
        }

        // 从发给 Model 的 Tool 列表中隐藏 Plan 模式禁用的 Tool；执行检查仍保留，防止 Model 强行调用。
        if (hasModeProvider)
        {
            chatClientBuilder.Use(static innerClient => new PlanModeToolVisibilityChatClient(innerClient));
        }

        // 每次请求 Model 前处理历史，包括加入新消息、整理顺序、保存进度和 Compaction。
        ConfigureHistoryPipeline(chatClientBuilder, definition, loggerFactory);

        // 移除空消息和已处理的 Function 审批响应，避免发送 Model 无法接收的内容；MCP 审批响应仍保留。
        chatClientBuilder.Use(static innerClient => new ModelInputFilteringChatClient(innerClient));
        // 用 OpenTelemetry 记录 Model 请求；EnableSensitiveData 允许记录请求和响应内容。
        chatClientBuilder.UseOpenTelemetry(
            sourceName: definition.OpenTelemetrySourceName,
            configure: static options => options.EnableSensitiveData = true
        );

        return chatClientBuilder.Build(services);
    }

    /// <summary>
    /// 配置每次 Model 请求的历史处理，包括加入新消息、保存记录和 Compaction。
    /// </summary>
    private static void ConfigureHistoryPipeline(
        ChatClientBuilder chatClientBuilder,
        ResolvedAgentDefinition definition,
        ILoggerFactory loggerFactory
    )
    {
        var compactionStateKey = definition.CompactionProvider is CompactionProvider compactionProvider
            ? compactionProvider.StateKeys.SingleOrDefault()
            : null;
        chatClientBuilder
            // 将执行期间追加的消息加入本次请求，一起整理并保存到历史。
            .UseMessageInjection()
            // 保存前先整理消息顺序，让 Tool 调用和对应结果相邻。
            .Use(static innerClient => new FunctionResultOrderingChatClient(innerClient))
            // 每次 Model 请求前加载历史，请求成功后保存新消息，保留 Function Loop 的中间进度。
            .UsePerServiceCallChatHistoryPersistence()
            // 加载历史后再次整理顺序，处理本次 Tool 结果对应历史中某次调用的情况。
            .Use(static innerClient => new FunctionResultOrderingChatClient(innerClient));
        if (definition.ChatHistoryProvider?.GetService<IStreamingConversationHistoryProvider>() is { } streamingHistory)
        {
            // Streaming 响应每收到一段内容就先保存再转发，避免必须等完整响应结束才能保存。
            chatClientBuilder.Use(innerClient => new StreamingChatHistoryClient(innerClient, streamingHistory));
        }
        if (definition.CompactionProvider != null)
        {
            chatClientBuilder
                // 临时调整 Session 信息，避免 SDK 把本地历史误认为由 Model 服务管理而跳过 Compaction；结束后恢复。
                .Use(innerClient => new LocalHistoryCompactionScopeChatClient(
                    innerClient,
                    compactionStateKey,
                    loggerFactory.CreateLogger<LocalHistoryCompactionScopeChatClient>()
                ))
                // 每次请求 Model 前，由 Compaction Provider 决定是否需要缩短历史内容。
                .UseAIContextProviders(definition.CompactionProvider);
        }
    }

    /// <summary>配置 Agent Pipeline，处理自动继续执行、状态通知、审批和 OpenTelemetry 记录。</summary>
    private static AIAgent BuildAgentPipeline(
        AIAgent innerAgent,
        ResolvedAgentDefinition definition,
        AgentCapabilityComposition capabilities,
        AgentModeProvider? modeProvider,
        ILoggerFactory loggerFactory,
        IServiceProvider services
    )
    {
        var agentBuilder = innerAgent.AsBuilder();
        if (capabilities.LoopEvaluators.Count > 0)
        {
            // LoopEvaluator 判断 Agent 是否需要继续执行；LoopAgent 最多允许 10 次迭代。
            agentBuilder.Use(
                (inner, _) =>
                    new LoopAgent(
                        inner,
                        capabilities.LoopEvaluators,
                        new LoopAgentOptions { MaxIterations = 10 },
                        loggerFactory
                    )
            );
        }

        ConfigureToolFeedback(agentBuilder, capabilities, modeProvider);

        var interactions =
            services.GetService(typeof(HumanInteractionContextAccessor)) as HumanInteractionContextAccessor;
        // 执行前刷新权限，并保存用户允许复用的授权；权限模式改变时清除失效授权。
        agentBuilder.Use((inner, _) => new MafApprovalGrantAgent(inner, interactions));
        // 尝试使用已有授权或自动审批规则；未通过的请求交给 AgentRuntime，决定直接放行还是等待用户。
        agentBuilder.UseToolApproval(
            new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [context => TryAutoApproveAsync(context, interactions, capabilities)],
            }
        );
        // 用 OpenTelemetry 记录 Agent 执行信息，导出位置由 Host 配置。
        agentBuilder.UseOpenTelemetry(sourceName: definition.OpenTelemetrySourceName);
        return agentBuilder.Build(services);
    }

    /// <summary>向客户端发送 Todo、Mode 的最新状态和 Tool 相关提示。</summary>
    private static void ConfigureToolFeedback(
        AIAgentBuilder agentBuilder,
        AgentCapabilityComposition capabilities,
        AgentModeProvider? modeProvider
    )
    {
        // 用 GetService 查找 AgwTodoProvider，即使它被其他 Provider 包装也能找到。
        var todoProvider = capabilities
            .ContextProviders.Select(static provider => provider.GetService<AgwTodoProvider>())
            .FirstOrDefault(static provider => provider != null);
        if (todoProvider != null)
        {
            var todoStateSnapshotMiddleware = new TodoStateSnapshotMiddleware(todoProvider);
            // Streaming 执行中，Todo 修改成功后发送最新列表，让客户端更新展示；普通执行不经过此处理。
            agentBuilder.Use(runFunc: null, runStreamingFunc: todoStateSnapshotMiddleware.RunStreamingAsync);
        }

        if (modeProvider != null)
        {
            var modeStateSnapshotMiddleware = new ModeStateSnapshotMiddleware(modeProvider);
            // mode_set 返回结果后发送 Session 中的实际模式，让客户端准确展示 Plan / Execute 状态。
            agentBuilder.Use(
                runFunc: modeStateSnapshotMiddleware.RunAsync,
                runStreamingFunc: modeStateSnapshotMiddleware.RunStreamingAsync
            );
        }

        if (capabilities.ToolWarnings.Count > 0)
        {
            var warningMiddleware = new ToolWarningMiddleware(capabilities.ToolWarnings);
            // 在响应开头发送准备 Tool 时产生的提示，无论该 Tool 是否被调用。
            agentBuilder.Use(
                runFunc: warningMiddleware.RunAsync,
                runStreamingFunc: warningMiddleware.RunStreamingAsync
            );
        }

        if (capabilities.ToolInvocationWarnings.Count > 0)
        {
            var invocationWarningMiddleware = new ToolInvocationWarningMiddleware(capabilities.ToolInvocationWarnings);
            // 只有 Tool 实际返回结果时才发送对应提示，例如本地搜索的降级说明；同一 CallId 只提示一次。
            agentBuilder.Use(
                runFunc: invocationWarningMiddleware.RunAsync,
                runStreamingFunc: invocationWarningMiddleware.RunStreamingAsync
            );
        }
    }

    /// <summary>判断当前 Tool 调用能否自动批准；返回 false 时由 AgentRuntime 继续处理审批。</summary>
    private static async ValueTask<bool> TryAutoApproveAsync(
        ToolAutoApprovalRuleContext context,
        HumanInteractionContextAccessor? interactions,
        AgentCapabilityComposition capabilities
    )
    {
        // 每次审批都读取最新权限；需要用户回答的问题不能自动批准，其他调用依次检查已有授权和自动审批规则。
        if (interactions is not null)
            await interactions.RefreshPermissionsAsync().ConfigureAwait(false);
        var source =
            context.RunOptions?.AdditionalProperties?.GetValueOrDefault(HumanInteractionToolMetadata.SourceKey)
            as InteractionSource;
        if (
            interactions?.Requests?.IsUserInputCall(source?.NodeId ?? "standalone", context.FunctionCallContent.CallId)
            == true
        )
            return false;
        var mode =
            context.Session is { } approvalSession && interactions?.PermissionState is { } permissions
                ? MafSessionApprovalState.Synchronize(approvalSession, permissions)
            : context.Session is { } existingSession ? MafSessionApprovalState.GetPermissionMode(existingSession)
            : null;
        if (MafSessionApprovalState.TryApprove(context, mode))
            return true;
        foreach (var rule in capabilities.AutoApprovalRules)
            if (await rule(context).ConfigureAwait(false))
                return true;
        return false;
    }
}
