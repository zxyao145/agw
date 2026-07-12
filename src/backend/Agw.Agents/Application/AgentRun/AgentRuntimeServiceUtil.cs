using Agw.Shared.Utils;

using ClaudeCodeSdk.MAF;

using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Application.AgentRun;

public static class AgentRuntimeServiceUtil
{
    public static string BuildInstructions(string? systemPrompt, string? workspace)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            systemPrompt = "You are an AI agent.";
        }

        if (string.IsNullOrWhiteSpace(workspace))
        {
            return systemPrompt;
        }

        workspace = PathUtil.ExpandTilde(workspace);
        var workspaceInstructions =
            $"""
            # others

            - Your default workspace or working directory is '{workspace}'.
            """;

        return $"{systemPrompt}{Environment.NewLine}{workspaceInstructions}";
    }


    #region CodexAgentOptions

    public static CodexAIAgentOptions? BuildCodexAIAgentOptions(
        string extra,
        string? workspace,
        Guid? threadId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync = null)
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
                ThreadOptions = CreateCodexThreadOptionsWithWorkspace(options.ThreadOptions, workspace)
            };
        }

        if (threadId != null)
        {
            options = options with
            {
                ThreadId = threadId.Value,
                IsResume = resume
            };
        }

        if (onThreadStartedAsync != null)
        {
            options = options with
            {
                OnThreadStartedAsync = onThreadStartedAsync
            };
        }

        if (environmentVariables is { Count: > 0 })
        {
            options = options with
            {
                CodexOptions = CreateCodexOptionsWithEnvironmentVariables(
                    options.CodexOptions,
                    environmentVariables)
            };
        }

        return options;
    }

    public static ClaudeCodeAIAgentOptions ApplyEnvironmentVariables(
        ClaudeCodeAIAgentOptions options,
        IReadOnlyDictionary<string, string>? environmentVariables)
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

        return options with { EnvironmentVariables = merged };
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
        IReadOnlyDictionary<string, string> environmentVariables)
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
            Env = merged
        };
    }

    #endregion
}
