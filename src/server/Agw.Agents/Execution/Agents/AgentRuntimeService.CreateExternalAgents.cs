using System.Diagnostics.CodeAnalysis;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.ExternalAgents;
using Agw.Agents.ExternalAgents.ClaudeCode;
using Agw.Files.Utils;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;
using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    private bool TryCreateExternalAgent(
        CreateAiAgentRequest request,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        [NotNullWhen(true)] out AIAgent? aiAgent,
        bool isBackground = false
    )
    {
        aiAgent = null;
        if (request.Agent.Type != AgentType.External)
        {
            return false;
        }

        aiAgent = request.Agent.Name switch
        {
            AgentNames.ClaudeCode => CreateClaudeCodeAgent(
                project,
                request.ProviderSessionId,
                request.IsResume,
                environmentVariables,
                isBackground
            ),
            AgentNames.Codex => CreateCodexAgent(
                project,
                request.ProviderSessionId,
                request.IsResume,
                environmentVariables,
                request.OnExternalSessionStartedAsync
            ),
            _ => null,
        };

        if (aiAgent != null)
        {
            aiAgent = IsClaudeCodeExternalAgent(request.Agent)
                ? WrapClaudeCodeAgent(aiAgent, isBackground, request.OnExternalSessionStartedAsync)
                : WrapExternalAgent(aiAgent, isBackground);
        }

        return aiAgent != null;
    }

    internal AIAgent WrapExternalAgent(AIAgent aiAgent, bool isBackground) =>
        DecorateExternalAgent(
            new ExternalAgentChatHistoryAgent(aiAgent, _chatHistoryProvider, _timeProvider, _logger),
            isBackground
        );

    internal AIAgent WrapClaudeCodeAgent(
        AIAgent aiAgent,
        bool isBackground,
        Func<string, CancellationToken, ValueTask>? onProviderSessionStartedAsync
    )
    {
        if (onProviderSessionStartedAsync != null)
        {
            aiAgent = new ClaudeCodeProviderSessionTrackingAgent(aiAgent, onProviderSessionStartedAsync);
        }

        return DecorateExternalAgent(aiAgent, isBackground);
    }

    private AIAgent DecorateExternalAgent(AIAgent aiAgent, bool isBackground)
    {
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
        Project project,
        Guid? providerSessionId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        bool isBackground
    )
    {
        string? extra = project.ExtraSetting;
        if (string.IsNullOrWhiteSpace(extra) || IsEmptyJsonObject(extra))
        {
            extra = JsonUtil.Serialize(
                new ClaudeCodeAIAgentOptions { PermissionMode = PermissionMode.bypassPermissions }
            );
        }

        var options = BuildClaudeCodeAIAgentOptions(
            extra,
            PathUtil.ExpandTilde(project.Workspace),
            providerSessionId,
            isResume,
            environmentVariables,
            new ClaudeCodeChatHistoryProvider(_chatHistoryProvider)
        );
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        var interactionBridge = new ClaudeCodeAskUserQuestionBridge(
            _humanInteractionContextAccessor,
            allowInteraction: !isBackground
        );
        options = options with { CanUseTool = interactionBridge.HandleAsync };
        return new ClaudeCodeAIAgent(options, _logger)
            .AsBuilder()
            .Use(runFunc: interactionBridge.BindRunAsync, runStreamingFunc: interactionBridge.BindRunStreamingAsync)
            .Build();
    }

    private static ClaudeCodeAIAgentOptions? BuildClaudeCodeAIAgentOptions(
        string extra,
        string? workspace,
        Guid? providerSessionId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ChatHistoryProvider? chatHistoryProvider = null
    )
    {
        var options = JsonUtil.Deserialize<ClaudeCodeAIAgentOptions>(extra);
        if (options == null)
        {
            return null;
        }

        options = options with
        {
            WorkingDirectory = workspace,
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
        Project project,
        Guid? threadId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync
    )
    {
        string? extra = project.ExtraSetting;
        if (string.IsNullOrWhiteSpace(extra) || IsEmptyJsonObject(extra))
        {
            extra = JsonUtil.Serialize(new CodexAIAgentOptions());
        }

        var options = BuildCodexAIAgentOptions(
            extra,
            PathUtil.ExpandTilde(project.Workspace),
            threadId,
            isResume,
            environmentVariables,
            onThreadStartedAsync
        );
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        options = DisableExternalSdkChatHistoryPersistence(options);
        return new CodexAIAgent(options, _logger);
    }

    internal static CodexAIAgentOptions DisableExternalSdkChatHistoryPersistence(CodexAIAgentOptions options) =>
        options with
        {
            ChatHistoryProvider = null,
        };

    #region CodexAgentOptions

    private static CodexAIAgentOptions? BuildCodexAIAgentOptions(
        string extra,
        string? workspace,
        Guid? threadId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync = null
    )
    {
        var options = JsonUtil.Deserialize<CodexAIAgentOptions>(extra);
        if (options == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            options = options with
            {
                ThreadOptions = CreateCodexThreadOptionsWithWorkspace(options.ThreadOptions, workspace),
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

    private static ThreadOptions CreateCodexThreadOptionsWithWorkspace(ThreadOptions? options, string workspace)
    {
        options ??= new ThreadOptions();

        return new ThreadOptions
        {
            Model = options.Model,
            SandboxMode = options.SandboxMode,
            WorkingDirectory = workspace,
            SkipGitRepoCheck = options.SkipGitRepoCheck,
            ModelReasoningEffort = options.ModelReasoningEffort,
            NetworkAccessEnabled = options.NetworkAccessEnabled,
            WebSearchMode = options.WebSearchMode,
            WebSearchEnabled = options.WebSearchEnabled,
            ApprovalPolicy = options.ApprovalPolicy,
            AdditionalDirectories = options.AdditionalDirectories,
        };
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

    internal static bool UsesProviderSessionBinding(Agent agent) =>
        IsClaudeCodeExternalAgent(agent) || IsCodexExternalAgent(agent);

    private static bool IsClaudeCodeExternalAgent(Agent agent) =>
        agent.Type == AgentType.External
        && string.Equals(agent.Name, AgentNames.ClaudeCode, StringComparison.OrdinalIgnoreCase);

    private static bool IsCodexExternalAgent(Agent agent) =>
        agent.Type == AgentType.External
        && string.Equals(agent.Name, AgentNames.Codex, StringComparison.OrdinalIgnoreCase);

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
