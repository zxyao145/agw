using System.Diagnostics.CodeAnalysis;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Context;
using Agw.Agents.Execution.Agents.Contracts;
using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Agents.Execution.Agents.ExternalAgents.Pi;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;
using Agw.Tools.Impl.ToolBlocks.UserMemory;
using ClaudeCodeSdk.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;
using PiAgentSdk;
using PiAgentSdk.MAF;

namespace Agw.Agents.Execution.Agents.Runtime;

public partial class AgentRuntimeService
{
    private static readonly HashSet<string> PiReservedEnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PI_CODING_AGENT_DIR",
        "PI_CODING_AGENT_SESSION_DIR",
        "PI_OFFLINE",
        "PI_SKIP_VERSION_CHECK",
        "PI_TELEMETRY",
    };

    private async Task<AIAgent?> CreateExternalAgentAsync(
        CreateAiAgentRequest request,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken,
        bool isBackground = false
    )
    {
        var modelConfiguration = await _agentAppService
            .GetExternalModelRuntimeConfigurationAsync(request.Agent.ExternalAgentKind, request.Agent.ModelProviderId)
            .ConfigureAwait(false);
        var capabilities = await _capabilityComposer
            .ComposeAsync(
                request.Agent,
                project,
                environmentVariables,
                cancellationToken,
                defaultMode: request.DefaultMode,
                conversationId: request.ConversationId,
                deferHumanInteractions: request.DeferHumanInteractions
            )
            .ConfigureAwait(false);
        AIAgent? aiAgent = null;
        try
        {
            var userMemoryProvider = capabilities
                .ContextProviders.Select(static provider => provider.GetService<UserMemoryProvider>())
                .SingleOrDefault(static provider => provider != null);
            Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync =
                userMemoryProvider == null ? null : userMemoryProvider.CreateContextMessageAsync;
            if (project.AdditionalDirectories.Count > 0)
            {
                var memoryContext = createMemoryContextAsync;
                createMemoryContextAsync = async token =>
                {
                    var memory = memoryContext == null ? null : await memoryContext(token).ConfigureAwait(false);
                    var context = new ChatMessage(
                        ChatRole.System,
                        $"Primary working directory: {project.Workspace}\nAdditional Project directories (file references use @<absolutePath>, with quotes around paths containing spaces):\n"
                            + string.Join(
                                "\n",
                                project.AdditionalDirectories.Select(directory =>
                                    $"- directoryId={directory.Id:D}: {directory.Path}"
                                )
                            )
                    );
                    if (memory != null)
                    {
                        foreach (var content in memory.Contents)
                        {
                            context.Contents.Add(content);
                        }
                    }
                    return context;
                };
            }
            if (
                !TryCreateExternalAgent(
                    request,
                    project,
                    environmentVariables,
                    _chatHistoryProvider,
                    createMemoryContextAsync,
                    out aiAgent,
                    isBackground,
                    modelConfiguration
                )
            )
            {
                await DisposeResourceWithoutThrowingAsync(capabilities).ConfigureAwait(false);
                return null;
            }

            return new ResourceOwningAIAgent(aiAgent!, capabilities);
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

    private bool TryCreateExternalAgent(
        CreateAiAgentRequest request,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        ChatHistoryProvider historyProvider,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync,
        [NotNullWhen(true)] out AIAgent? aiAgent,
        bool isBackground,
        AgentModelRuntimeConfiguration? modelConfiguration
    )
    {
        ExecutionPermissionService.Validate(request.Agent, request.PermissionMode);
        var kind = ExternalAgentKindResolver.Resolve(request.Agent);
        aiAgent = kind switch
        {
            ExternalAgentKind.ClaudeCode => CreateClaudeCodeAgent(
                request.Agent,
                project,
                request.ProviderSessionId,
                request.IsResume,
                environmentVariables,
                isBackground,
                historyProvider,
                request.PermissionMode,
                modelConfiguration
            ),
            ExternalAgentKind.Codex => CreateCodexAgent(
                request.Agent,
                project,
                request.ProviderSessionId,
                request.IsResume,
                environmentVariables,
                request.OnExternalSessionStartedAsync,
                request.PermissionMode,
                modelConfiguration
            ),
            ExternalAgentKind.Pi => CreatePiAgent(
                request.Agent,
                project,
                request.ProviderSessionId,
                request.IsResume,
                environmentVariables,
                request.OnExternalSessionStartedAsync,
                isBackground,
                historyProvider,
                createMemoryContextAsync,
                modelConfiguration
            ),
            _ => null,
        };

        if (aiAgent == null)
        {
            return false;
        }

        aiAgent = kind switch
        {
            ExternalAgentKind.ClaudeCode => WrapClaudeCodeAgent(
                aiAgent,
                historyProvider,
                isBackground,
                request.OnExternalSessionStartedAsync,
                createMemoryContextAsync
            ),
            ExternalAgentKind.Codex => WrapExternalAgent(
                aiAgent,
                historyProvider,
                isBackground,
                createMemoryContextAsync
            ),
            ExternalAgentKind.Pi => aiAgent,
            _ => null,
        };
        return aiAgent != null;
    }

    internal AIAgent WrapExternalAgent(
        AIAgent aiAgent,
        bool isBackground,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync = null
    ) => WrapExternalAgent(aiAgent, _chatHistoryProvider, isBackground, createMemoryContextAsync);

    private AIAgent WrapExternalAgent(
        AIAgent aiAgent,
        ChatHistoryProvider historyProvider,
        bool isBackground,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync
    ) =>
        DecorateExternalAgent(
            new ExternalAgentChatHistoryAgent(aiAgent, historyProvider, _timeProvider, _logger),
            historyProvider,
            isBackground,
            createMemoryContextAsync
        );

    internal AIAgent WrapClaudeCodeAgent(
        AIAgent aiAgent,
        bool isBackground,
        Func<string, CancellationToken, ValueTask>? onProviderSessionStartedAsync,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync = null
    ) =>
        WrapClaudeCodeAgent(
            aiAgent,
            _chatHistoryProvider,
            isBackground,
            onProviderSessionStartedAsync,
            createMemoryContextAsync
        );

    private AIAgent WrapClaudeCodeAgent(
        AIAgent aiAgent,
        ChatHistoryProvider historyProvider,
        bool isBackground,
        Func<string, CancellationToken, ValueTask>? onProviderSessionStartedAsync,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync
    )
    {
        var ownedResource = aiAgent as IAsyncDisposable;
        if (onProviderSessionStartedAsync != null)
        {
            aiAgent = new ClaudeCodeProviderSessionTrackingAgent(aiAgent, onProviderSessionStartedAsync);
        }

        var decorated = DecorateExternalAgent(aiAgent, historyProvider, isBackground, createMemoryContextAsync);
        return ownedResource == null ? decorated : new ResourceOwningAIAgent(decorated, ownedResource);
    }

    internal AIAgent WrapPiAgent(
        AIAgent aiAgent,
        bool isBackground,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync = null
    ) => DecorateExternalAgent(aiAgent, _chatHistoryProvider, isBackground, createMemoryContextAsync);

    internal AIAgent WrapPiAgent(AIAgent aiAgent, IAsyncDisposable ownedResource, bool isBackground) =>
        WrapPiAgent(aiAgent, ownedResource, _chatHistoryProvider, isBackground, createMemoryContextAsync: null);

    private AIAgent WrapPiAgent(
        AIAgent aiAgent,
        IAsyncDisposable ownedResource,
        ChatHistoryProvider historyProvider,
        bool isBackground,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync
    ) =>
        new ResourceOwningAIAgent(
            DecorateExternalAgent(aiAgent, historyProvider, isBackground, createMemoryContextAsync),
            ownedResource
        );

    private AIAgent DecorateExternalAgent(
        AIAgent aiAgent,
        ChatHistoryProvider historyProvider,
        bool isBackground,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync
    )
    {
        aiAgent = new AgentRequestContextAgent(aiAgent, historyProvider, createMemoryContextAsync, _logger);

        var agentBuilder = aiAgent
            .AsBuilder()
            .Use(
                runFunc: _observabilityMiddleware.LogRunMiddleware,
                runStreamingFunc: _observabilityMiddleware.LogStreamingMiddleware
            )
            .Use(
                runFunc: _usageTrackingMiddleware.TrackRunMiddleware,
                runStreamingFunc: _usageTrackingMiddleware.TrackStreamingMiddleware
            );
        if (isBackground)
        {
            var approvalMiddleware = new BackgroundAgentApprovalMiddleware(_humanInteractionContextAccessor);
            agentBuilder.Use(
                runFunc: approvalMiddleware.RejectNewApprovalAsync,
                runStreamingFunc: approvalMiddleware.RejectNewApprovalStreamingAsync
            );
        }

        return agentBuilder.Build();
    }

    private AIAgent? CreateClaudeCodeAgent(
        Agent agent,
        Project project,
        Guid? providerSessionId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        bool isBackground,
        ChatHistoryProvider historyProvider,
        AgwPermissionMode? permissionMode,
        AgentModelRuntimeConfiguration? modelConfiguration
    )
    {
        var options = BuildClaudeCodeAIAgentOptions(
            agent,
            project,
            providerSessionId,
            isResume,
            environmentVariables,
            new ClaudeCodeChatHistoryProvider(historyProvider),
            permissionMode
        );
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        var interactionBridge = new ClaudeCodeAskUserQuestionBridge(
            _humanInteractionContextAccessor,
            allowInteraction: !isBackground,
            permissionMode: permissionMode,
            workingDirectory: options.WorkingDirectory,
            agentId: agent.Id,
            turnContext: _turnContextAccessor,
            cache: _claudeApprovals
        );
        options = ExternalAgentModelOptions.ApplyClaudeCode(options, modelConfiguration);
        options = options with { CanUseTool = interactionBridge.HandleAsync };
        var modelSettings = modelConfiguration == null ? null : ClaudeCodeModelSettings.Create(options);
        try
        {
            var aiAgent = new ClaudeCodeAIAgent(modelSettings?.Options ?? options, _logger)
                .AsBuilder()
                .Use(runFunc: interactionBridge.BindRunAsync, runStreamingFunc: interactionBridge.BindRunStreamingAsync)
                .Build();
            return modelSettings == null ? aiAgent : new ResourceOwningAIAgent(aiAgent, modelSettings);
        }
        catch
        {
            modelSettings?.Dispose();
            throw;
        }
    }

    private static ClaudeCodeAIAgentOptions? BuildClaudeCodeAIAgentOptions(
        Agent agent,
        Project project,
        Guid? providerSessionId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ChatHistoryProvider? chatHistoryProvider = null,
        AgwPermissionMode? permissionMode = null
    )
    {
        var extra = agent.Extra;
        if (string.IsNullOrWhiteSpace(extra) || IsEmptyJsonObject(extra))
        {
            extra = ExternalAgentDefaults.GetDefaultExtra(ExternalAgentKind.ClaudeCode);
        }

        var options = JsonUtil.Deserialize<ClaudeCodeAIAgentOptions>(extra);
        if (options == null)
        {
            return null;
        }

        options = options with
        {
            PermissionMode =
                permissionMode == AgwPermissionMode.FullAccess ? ClaudeCodeSdk.Types.PermissionMode.bypassPermissions
                : permissionMode.HasValue ? ClaudeCodeSdk.Types.PermissionMode.@default
                : options.PermissionMode,
            WorkingDirectory = PathUtil.ExpandTilde(project.Workspace),
            AddDirectories = MergeAdditionalDirectories(
                options.AddDirectories,
                project.AdditionalDirectories,
                project.Workspace
            ),
            IncludePartialMessages = true,
            ContinueConversation = false,
            Resume = null,
            SessionId = null,
            ChatHistoryProvider = chatHistoryProvider,
        };

        if (providerSessionId.HasValue)
        {
            options = isResume
                ? options with
                {
                    Resume = providerSessionId.Value.Normalize(),
                }
                : options with
                {
                    SessionId = providerSessionId.Value,
                };
        }

        return ApplyEnvironmentVariables(options, environmentVariables);
    }

    private AIAgent? CreateCodexAgent(
        Agent agent,
        Project project,
        Guid? threadId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync,
        AgwPermissionMode? permissionMode,
        AgentModelRuntimeConfiguration? modelConfiguration
    )
    {
        var options = BuildCodexAIAgentOptions(
            agent,
            project,
            threadId,
            isResume,
            environmentVariables,
            onThreadStartedAsync,
            permissionMode
        );
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        options = ExternalAgentModelOptions.ApplyCodex(options, modelConfiguration);
        options = DisableExternalSdkChatHistoryPersistence(options);
        return new CodexAIAgent(options, _logger);
    }

    private AIAgent? CreatePiAgent(
        Agent agent,
        Project project,
        Guid? providerSessionId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Func<string, CancellationToken, ValueTask>? onSessionStartedAsync,
        bool isBackground,
        ChatHistoryProvider historyProvider,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync,
        AgentModelRuntimeConfiguration? modelConfiguration
    )
    {
        var paths = PiRuntimePaths.Create(_dataPaths, ResolveExecutionUserId());
        paths.EnsureCreated();
        var interactionBridge = new PiExtensionUiBridge(
            _humanInteractionContextAccessor,
            allowInteraction: !isBackground
        );
        var options = BuildPiAgentAIAgentOptions(
            agent,
            project,
            paths.ConfigDirectory,
            paths.SessionDirectory,
            providerSessionId,
            isResume,
            environmentVariables,
            new PiChatHistoryProvider(historyProvider),
            interactionBridge.HandleAsync,
            onSessionStartedAsync
        );
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to Pi options error");
            return null;
        }

        options = ExternalAgentModelOptions.ApplyPi(options, modelConfiguration);
        var piAgent = new PiAgentAIAgent(options, _logger);
        var interactionAgent = piAgent
            .AsBuilder()
            .Use(runFunc: interactionBridge.BindRunAsync, runStreamingFunc: interactionBridge.BindRunStreamingAsync)
            .Build();
        // MAF builder proxies do not retain IAsyncDisposable, so keep the concrete process owner outside the full chain.
        return WrapPiAgent(interactionAgent, piAgent, historyProvider, isBackground, createMemoryContextAsync);
    }

    internal static PiAgentAIAgentOptions? BuildPiAgentAIAgentOptions(
        Agent agent,
        Project project,
        string configDirectory,
        string sessionDirectory,
        Guid? providerSessionId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        ChatHistoryProvider chatHistoryProvider,
        Func<PiExtensionUiRequest, CancellationToken, ValueTask<PiExtensionUiResponse>> extensionUiHandler,
        Func<string, CancellationToken, ValueTask>? onSessionStartedAsync
    )
    {
        var extra = agent.Extra;
        if (string.IsNullOrWhiteSpace(extra) || IsEmptyJsonObject(extra))
        {
            extra = ExternalAgentDefaults.GetDefaultExtra(ExternalAgentKind.Pi);
        }

        var options = JsonUtil.Deserialize<PiAgentAIAgentOptions>(extra);
        if (options == null)
        {
            return null;
        }

        var globalOptions = options.GlobalOptions ?? new PiAgentOptions();
        var sessionOptions = options.SessionOptions ?? new PiSessionOptions();
        var mergedEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        MergePiEnvironment(mergedEnvironment, globalOptions.EnvironmentVariables);
        MergePiEnvironment(mergedEnvironment, sessionOptions.EnvironmentVariables);
        MergePiEnvironment(mergedEnvironment, environmentVariables);
        mergedEnvironment["PI_CODING_AGENT_DIR"] = configDirectory;
        mergedEnvironment["PI_CODING_AGENT_SESSION_DIR"] = sessionDirectory;
        mergedEnvironment["PI_OFFLINE"] = "1";
        mergedEnvironment["PI_SKIP_VERSION_CHECK"] = "1";
        mergedEnvironment["PI_TELEMETRY"] = "0";

        globalOptions = globalOptions with { EnvironmentVariables = null };
        sessionOptions = sessionOptions with
        {
            WorkingDirectory = PathUtil.ExpandTilde(project.Workspace),
            SessionDir = sessionDirectory,
            NoSession = false,
            NoExtensions = true,
            EnvironmentVariables = mergedEnvironment,
            ExtensionUiHandler = extensionUiHandler,
        };

        return options with
        {
            GlobalOptions = globalOptions,
            SessionOptions = sessionOptions,
            SessionId = providerSessionId?.ToString("D"),
            IsResume = providerSessionId.HasValue && isResume,
            ChatHistoryProvider = chatHistoryProvider,
            OnSessionStartedAsync = onSessionStartedAsync,
        };
    }

    private static void MergePiEnvironment(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string>? source
    )
    {
        if (source == null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            if (!PiReservedEnvironmentKeys.Contains(key))
            {
                target[key] = value;
            }
        }
    }

    internal static CodexAIAgentOptions DisableExternalSdkChatHistoryPersistence(CodexAIAgentOptions options) =>
        options with
        {
            ChatHistoryProvider = null,
        };

    #region CodexAgentOptions

    private static CodexAIAgentOptions? BuildCodexAIAgentOptions(
        Agent agent,
        Project project,
        Guid? threadId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync = null,
        AgwPermissionMode? permissionMode = null
    )
    {
        var extra = agent.Extra;
        if (string.IsNullOrWhiteSpace(extra) || IsEmptyJsonObject(extra))
        {
            extra = ExternalAgentDefaults.GetDefaultExtra(ExternalAgentKind.Codex);
        }

        ExecutionPermissionService.Validate(agent, permissionMode);
        var options = JsonUtil.Deserialize<CodexAIAgentOptions>(extra);
        if (options == null)
        {
            return null;
        }

        var workspace = PathUtil.ExpandTilde(project.Workspace);
        if (!string.IsNullOrWhiteSpace(workspace) || permissionMode == AgwPermissionMode.FullAccess)
        {
            options = options with
            {
                ThreadOptions = CreateCodexThreadOptionsWithWorkspace(
                    options.ThreadOptions,
                    workspace,
                    permissionMode,
                    project.AdditionalDirectories
                ),
            };
        }

        if (threadId != null)
        {
            options = options with { ThreadId = threadId.Value, IsResume = isResume };
        }

        if (onThreadStartedAsync != null)
        {
            options = options with { OnThreadStartedAsync = onThreadStartedAsync };
        }

        if (environmentVariables is { Count: > 0 })
        {
            options = options with
            {
                CodexOptions = CreateCodexOptionsWithEnvironmentVariables(options.CodexOptions, environmentVariables),
            };
        }

        return options;
    }

    public static ClaudeCodeAIAgentOptions ApplyEnvironmentVariables(
        ClaudeCodeAIAgentOptions options,
        IReadOnlyDictionary<string, string>? environmentVariables
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        if (environmentVariables is not { Count: > 0 })
        {
            return options;
        }

        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (options.EnvironmentVariables != null)
        {
            foreach (var (key, value) in options.EnvironmentVariables)
            {
                merged[key] = value;
            }
        }

        foreach (var (key, value) in environmentVariables)
        {
            merged[key] = value;
        }

        return options with
        {
            EnvironmentVariables = merged,
        };
    }

    private static ThreadOptions CreateCodexThreadOptionsWithWorkspace(
        ThreadOptions? options,
        string? workspace,
        AgwPermissionMode? permissionMode = null,
        IReadOnlyList<ProjectDirectory>? additionalDirectories = null
    )
    {
        options ??= new ThreadOptions();

        return new ThreadOptions
        {
            Model = options.Model,
            SandboxMode =
                permissionMode == AgwPermissionMode.FullAccess ? SandboxMode.DangerFullAccess : options.SandboxMode,
            WorkingDirectory = string.IsNullOrWhiteSpace(workspace) ? options.WorkingDirectory : workspace,
            SkipGitRepoCheck = options.SkipGitRepoCheck,
            ModelReasoningEffort = options.ModelReasoningEffort,
            NetworkAccessEnabled = options.NetworkAccessEnabled,
            WebSearchMode = options.WebSearchMode,
            WebSearchEnabled = options.WebSearchEnabled,
            ApprovalPolicy =
                permissionMode == AgwPermissionMode.FullAccess ? ApprovalMode.Never : options.ApprovalPolicy,
            AdditionalDirectories = MergeAdditionalDirectories(
                options.AdditionalDirectories,
                additionalDirectories ?? [],
                workspace
            ),
        };
    }

    internal static IReadOnlyList<string> MergeAdditionalDirectories(
        IEnumerable<string>? configured,
        IReadOnlyList<ProjectDirectory> directories,
        string? workspace
    )
    {
        var root = string.IsNullOrWhiteSpace(workspace)
            ? Environment.CurrentDirectory
            : ProjectWorkspacePaths.Normalize(workspace);
        return (configured ?? [])
            .Concat(directories.Select(directory => directory.Path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(PathUtil.ExpandTilde(path), root)))
            .Distinct(ProjectWorkspacePaths.Comparer)
            .ToArray();
    }

    private static CodexOptions CreateCodexOptionsWithEnvironmentVariables(
        CodexOptions? options,
        IReadOnlyDictionary<string, string> environmentVariables
    )
    {
        options ??= new CodexOptions();

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.Env != null)
        {
            foreach (var (key, value) in options.Env)
            {
                merged[key] = value;
            }
        }
        else
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    merged[key] = value;
                }
            }
        }

        foreach (var (key, value) in environmentVariables)
        {
            merged[key] = value;
        }

        return new CodexOptions
        {
            CodexPathOverride = options.CodexPathOverride,
            BaseUrl = options.BaseUrl,
            ApiKey = options.ApiKey,
            Config = options.Config,
            Env = merged,
        };
    }

    #endregion

    private static bool IsEmptyJsonObject(string value)
    {
        var hasOpen = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (!hasOpen)
            {
                if (c != '{')
                    return false;
                hasOpen = true;
                continue;
            }

            if (c != '}')
                return false;
            return true;
        }

        return false;
    }
}
