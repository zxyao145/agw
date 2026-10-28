using System.Text.Json;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.ExternalAgents.Codex;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Agents.AI;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.MAF;
using OpenAI.CodexSdk.AppServer.Protocol;
using Project = Agw.Shared.Data.Entities.Projects.Project;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Codex;

internal static class CodexAgentRuntimeOptionsMapper
{
    public static CodexAgentRuntimeOptions? Build(
        string extra,
        Project project,
        Guid? providerSessionId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync,
        AgwPermissionMode? permissionMode
    )
    {
        var settings = CodexAgentSettings.Deserialize(extra);
        if (settings == null)
        {
            return null;
        }

        if (settings.OnThreadStartedAsync is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Codex Extra cannot configure OnThreadStartedAsync; session binding is managed by Agw."
            );
        }

        if (settings.ChatHistoryProvider is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Codex Extra cannot configure ChatHistoryProvider; history is managed by Agw."
            );
        }

        var clientSettings = settings.CodexOptions;
        var threadSettings = settings.ThreadOptions;
        if (threadSettings.SkipGitRepoCheck == false)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Codex app-server does not support skipGitRepoCheck=false."
            );
        }

        var selectedProviderSessionId = providerSessionId ?? settings.ThreadId;
        var selectedResume = providerSessionId.HasValue ? isResume : settings.IsResume;
        var workspace = PathUtil.ExpandTilde(project.Workspace);
        var workingDirectory = string.IsNullOrWhiteSpace(workspace)
            ? PathUtil.ExpandTilde(threadSettings.WorkingDirectory)
            : workspace;
        var additionalDirectories = AgentRuntimeFactory.MergeAdditionalDirectories(
            threadSettings.AdditionalDirectories,
            project.AdditionalDirectories,
            workingDirectory
        );

        var config = ReadConfig(clientSettings.Config);
        var writableRoots = MergeWritableRoots(config, additionalDirectories);
        if (writableRoots.Count > 0)
        {
            config["sandbox_workspace_write.writable_roots"] = JsonSerializer.SerializeToElement(writableRoots);
        }

        if (threadSettings.NetworkAccessEnabled is { } networkAccessEnabled)
        {
            config["sandbox_workspace_write.network_access"] = JsonSerializer.SerializeToElement(networkAccessEnabled);
        }

        if (threadSettings.ModelReasoningEffort is { } reasoningEffort)
        {
            config["model_reasoning_effort"] = JsonSerializer.SerializeToElement(ToConfigValue(reasoningEffort));
        }

        if (threadSettings.ModelContextWindow is > 0)
        {
            config["model_context_window"] = JsonSerializer.SerializeToElement(threadSettings.ModelContextWindow.Value);
        }

        if (threadSettings.ModelAutoCompactTokenLimit is > 0)
        {
            config["model_auto_compact_token_limit"] = JsonSerializer.SerializeToElement(
                threadSettings.ModelAutoCompactTokenLimit.Value
            );
        }

        if (threadSettings.WebSearchMode is { } webSearchMode)
        {
            config["web_search"] = JsonSerializer.SerializeToElement(ToConfigValue(webSearchMode));
        }
        else if (threadSettings.WebSearchEnabled is { } webSearchEnabled)
        {
            config["web_search"] = JsonSerializer.SerializeToElement(webSearchEnabled ? "live" : "disabled");
        }

        var toolEnvironment = MergeToolEnvironment(clientSettings.Env, environmentVariables);
        if (toolEnvironment.Keys.Any(key => string.Equals(key, "CODEX_HOME", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Codex app-server cannot apply CODEX_HOME from Codex environment settings. Configure the host Codex home directly."
            );
        }

        var configuredApiKey =
            clientSettings.ApiKey
            ?? GetEnvironmentValue(toolEnvironment, "CODEX_API_KEY")
            ?? GetEnvironmentValue(toolEnvironment, "OPENAI_API_KEY");
        var configuredBaseUrl = string.IsNullOrWhiteSpace(clientSettings.BaseUrl) ? null : clientSettings.BaseUrl;
        if (configuredApiKey != null || configuredBaseUrl != null)
        {
            SetProviderConfig(config, configuredBaseUrl, configuredApiKey);
        }

        SetToolEnvironment(config, toolEnvironment);

        var sandbox =
            permissionMode == AgwPermissionMode.FullAccess
                ? SandboxMode.DangerFullAccess
                : MapSandbox(threadSettings.SandboxMode);
        var approvalPolicy =
            permissionMode == AgwPermissionMode.FullAccess ? CreateApprovalPolicy(CodexApprovalMode.Never)
            : threadSettings.ApprovalPolicy is { } approvalMode ? CreateApprovalPolicy(approvalMode)
            : default;
        var runtimeRoots = new[] { workingDirectory }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Concat(writableRoots)
            .Distinct(ProjectWorkspacePaths.Comparer)
            .ToArray();
        var threadStartParams = new ThreadStartParams
        {
            Model = threadSettings.Model,
            ModelProvider =
                configuredApiKey != null || configuredBaseUrl != null ? new Optional<string?>("agw") : default,
            Cwd = workingDirectory,
            Sandbox = sandbox,
            ApprovalPolicy = approvalPolicy,
            RuntimeWorkspaceRoots =
                runtimeRoots.Length == 0 ? default : new Optional<IReadOnlyList<string>?>(runtimeRoots),
            Config = config.Count == 0 ? default : new Optional<IReadOnlyDictionary<string, JsonElement>?>(config),
        };
        var configureTurnStart = CreateTurnStartConfigurator(settings.TurnOptions?.OutputSchema);
        var agentOptions = new CodexAppServerAIAgentOptions
        {
            ThreadStartParams = threadStartParams,
            ConfigureThreadResume = parameters => ConfigureThreadResume(parameters, threadStartParams),
            ConfigureTurnStart = configureTurnStart,
        };
        var clientOptions = new CodexAppServerClientOptions
        {
            ExecutablePath = clientSettings.CodexPathOverride ?? "codex",
            WorkingDirectory = workingDirectory,
            // 工作区根目录参数需要显式声明能力。Workspace roots require this explicit capability.
            Capabilities = new() { ExperimentalApi = threadStartParams.RuntimeWorkspaceRoots.IsDefined },
        };

        return new CodexAgentRuntimeOptions(
            clientOptions,
            agentOptions,
            selectedProviderSessionId,
            selectedResume,
            onThreadStartedAsync
        );
    }

    internal static ThreadResumeParams ConfigureThreadResume(
        ThreadResumeParams parameters,
        ThreadStartParams threadStartParams
    ) =>
        parameters with
        {
            ModelProvider = threadStartParams.ModelProvider,
            Model = threadStartParams.Model,
            Config = threadStartParams.Config,
            Cwd = threadStartParams.Cwd,
            RuntimeWorkspaceRoots = threadStartParams.RuntimeWorkspaceRoots,
            Sandbox = threadStartParams.Sandbox,
            ApprovalPolicy = threadStartParams.ApprovalPolicy,
            BaseInstructions = threadStartParams.BaseInstructions,
            DeveloperInstructions = threadStartParams.DeveloperInstructions,
            Permissions = threadStartParams.Permissions,
        };

    internal static IReadOnlyDictionary<string, JsonElement> MergeProviderConfig(
        IReadOnlyDictionary<string, JsonElement>? original,
        string? baseUrl,
        string apiKey
    )
    {
        var config =
            original == null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(original, StringComparer.Ordinal);
        SetProviderConfig(config, baseUrl, apiKey);
        return config;
    }

    private static Func<AgentRunOptions?, TurnStartParams, TurnStartParams>? CreateTurnStartConfigurator(
        JsonElement? outputSchema
    )
    {
        if (outputSchema is not { ValueKind: JsonValueKind.Object } schema)
        {
            if (
                outputSchema.HasValue
                && outputSchema.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            )
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Codex turnOptions.outputSchema must be a JSON object."
                );
            }

            return null;
        }

        var schemaCopy = schema.Clone();
        return (_, parameters) => parameters with { OutputSchema = schemaCopy };
    }

    private static Dictionary<string, JsonElement> ReadConfig(JsonElement? legacyConfig)
    {
        Dictionary<string, JsonElement> config = new(StringComparer.Ordinal);
        if (legacyConfig is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } root)
        {
            return config;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Codex codexOptions.config must be a JSON object.");
        }

        var entries = root;
        if (TryGetWrapperObject(entries, "properties", out var wrapperProperties))
        {
            entries = wrapperProperties;
        }

        foreach (var property in entries.EnumerateObject())
        {
            FlattenConfig(property.Name, property.Value, config);
        }

        return config;
    }

    private static void FlattenConfig(string path, JsonElement value, Dictionary<string, JsonElement> config)
    {
        if (TryGetWrapperObject(value, "properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                FlattenConfig($"{path}.{property.Name}", property.Value, config);
            }
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (TryGetWrapperValue(value, out var wrappedValue))
            {
                config[path] = NormalizeLegacyValue(wrappedValue);
                return;
            }

            if (TryGetWrapperArray(value, out var items))
            {
                config[path] = NormalizeLegacyArray(items);
                return;
            }

            foreach (var property in value.EnumerateObject())
            {
                FlattenConfig($"{path}.{property.Name}", property.Value, config);
            }
            return;
        }

        config[path] = NormalizeLegacyValue(value);
    }

    private static JsonElement NormalizeLegacyValue(JsonElement value)
    {
        if (TryGetWrapperValue(value, out var wrappedValue))
        {
            return NormalizeLegacyValue(wrappedValue);
        }

        if (TryGetWrapperArray(value, out var items))
        {
            return NormalizeLegacyArray(items);
        }

        if (TryGetWrapperObject(value, "properties", out var properties))
        {
            Dictionary<string, JsonElement> normalized = new(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateObject())
            {
                normalized[property.Name] = NormalizeLegacyValue(property.Value);
            }
            return JsonSerializer.SerializeToElement(normalized);
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return NormalizeLegacyArray(value);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> normalized = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                normalized[property.Name] = NormalizeLegacyValue(property.Value);
            }
            return JsonSerializer.SerializeToElement(normalized);
        }

        return value.Clone();
    }

    private static JsonElement NormalizeLegacyArray(JsonElement items) =>
        JsonSerializer.SerializeToElement(items.EnumerateArray().Select(NormalizeLegacyValue).ToArray());

    private static bool TryGetWrapperObject(JsonElement value, string propertyName, out JsonElement properties)
    {
        properties = default;
        return value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Count() == 1
            && value.TryGetProperty(propertyName, out properties)
            && properties.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetWrapperArray(JsonElement value, out JsonElement items)
    {
        items = default;
        return value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Count() == 1
            && value.TryGetProperty("items", out items)
            && items.ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetWrapperValue(JsonElement value, out JsonElement wrappedValue)
    {
        wrappedValue = default;
        return value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Count() == 1
            && value.TryGetProperty("value", out wrappedValue);
    }

    private static IReadOnlyList<string> MergeWritableRoots(
        IDictionary<string, JsonElement> config,
        IReadOnlyList<string> additionalDirectories
    )
    {
        List<string> roots = [];
        if (config.TryGetValue("sandbox_workspace_write.writable_roots", out var configuredRoots))
        {
            if (configuredRoots.ValueKind != JsonValueKind.Array)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Codex sandbox_workspace_write.writable_roots must be an array of paths."
                );
            }

            foreach (var root in configuredRoots.EnumerateArray())
            {
                if (root.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(root.GetString()))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        "Codex sandbox_workspace_write.writable_roots must contain non-empty path strings."
                    );
                }

                roots.Add(Path.GetFullPath(PathUtil.ExpandTilde(root.GetString()!)));
            }
        }

        roots.AddRange(additionalDirectories);
        return roots.Distinct(ProjectWorkspacePaths.Comparer).ToArray();
    }

    private static Dictionary<string, string> MergeToolEnvironment(
        IReadOnlyDictionary<string, string>? configuredEnvironment,
        IReadOnlyDictionary<string, string>? runtimeEnvironment
    )
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        if (configuredEnvironment != null)
        {
            foreach (var (key, value) in configuredEnvironment)
            {
                environment[key] = value;
            }
        }

        if (runtimeEnvironment != null)
        {
            foreach (var (key, value) in runtimeEnvironment)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    private static void SetToolEnvironment(
        IDictionary<string, JsonElement> config,
        IReadOnlyDictionary<string, string> environment
    )
    {
        Dictionary<string, string> merged = new(StringComparer.Ordinal);
        if (config.TryGetValue("shell_environment_policy.set", out var configured))
        {
            if (configured.ValueKind != JsonValueKind.Object)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Codex shell_environment_policy.set must be an object of environment variable values."
                );
            }

            foreach (var property in configured.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Codex tool environment value '{property.Name}' must be a string."
                    );
                }

                merged[property.Name] = property.Value.GetString()!;
            }
        }

        const string setPrefix = "shell_environment_policy.set.";
        foreach (var key in config.Keys.Where(key => key.StartsWith(setPrefix, StringComparison.Ordinal)).ToArray())
        {
            var propertyName = key[setPrefix.Length..];
            var value = config[key];
            if (propertyName.Length == 0 || value.ValueKind != JsonValueKind.String)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Codex shell_environment_policy.set values must be strings with environment variable names."
                );
            }

            merged[propertyName] = value.GetString()!;
            config.Remove(key);
        }

        foreach (var (key, value) in environment)
        {
            merged[key] = value;
        }

        if (merged.Count > 0)
        {
            config["shell_environment_policy.set"] = JsonSerializer.SerializeToElement(merged);
        }
    }

    private static string? GetEnvironmentValue(IReadOnlyDictionary<string, string> environment, string name) =>
        environment.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void SetProviderConfig(IDictionary<string, JsonElement> config, string? baseUrl, string? apiKey)
    {
        config["model_provider"] = JsonSerializer.SerializeToElement("agw");
        config["model_providers.agw.name"] = JsonSerializer.SerializeToElement("Agw");
        config["model_providers.agw.wire_api"] = JsonSerializer.SerializeToElement("responses");
        config["model_providers.agw.requires_openai_auth"] = JsonSerializer.SerializeToElement(false);
        if (baseUrl != null)
        {
            config["model_providers.agw.base_url"] = JsonSerializer.SerializeToElement(baseUrl);
        }
        if (apiKey != null)
        {
            config["model_providers.agw.experimental_bearer_token"] = JsonSerializer.SerializeToElement(apiKey);
        }
        else
        {
            config["model_providers.agw.env_key"] = JsonSerializer.SerializeToElement("OPENAI_API_KEY");
        }
    }

    private static SandboxMode? MapSandbox(CodexSandboxMode? sandboxMode) =>
        sandboxMode switch
        {
            CodexSandboxMode.ReadOnly => SandboxMode.ReadOnly,
            CodexSandboxMode.WorkspaceWrite => SandboxMode.WorkspaceWrite,
            CodexSandboxMode.DangerFullAccess => SandboxMode.DangerFullAccess,
            _ => null,
        };

    private static AskForApproval CreateApprovalPolicy(CodexApprovalMode approvalMode) =>
        approvalMode switch
        {
            CodexApprovalMode.Never => new StringAskForApproval { Value = StringAskForApprovalValue.Never },
            CodexApprovalMode.OnRequest => new StringAskForApproval { Value = StringAskForApprovalValue.OnRequest },
            CodexApprovalMode.Untrusted => new StringAskForApproval { Value = StringAskForApprovalValue.Untrusted },
            CodexApprovalMode.OnFailure => throw new AgwException(
                ErrorCodes.InvalidParam,
                "Codex app-server does not support the legacy onFailure approval policy."
            ),
            _ => throw new AgwException(ErrorCodes.InvalidParam, "The Codex approval policy is not supported."),
        };

    private static string ToConfigValue(CodexModelReasoningEffort effort) =>
        effort switch
        {
            CodexModelReasoningEffort.Minimal => "minimal",
            CodexModelReasoningEffort.Low => "low",
            CodexModelReasoningEffort.Medium => "medium",
            CodexModelReasoningEffort.High => "high",
            CodexModelReasoningEffort.XHigh => "xhigh",
            _ => throw new AgwException(ErrorCodes.InvalidParam, "The Codex reasoning effort is not supported."),
        };

    private static string ToConfigValue(CodexWebSearchMode mode) =>
        mode switch
        {
            CodexWebSearchMode.Disabled => "disabled",
            CodexWebSearchMode.Cached => "cached",
            CodexWebSearchMode.Live => "live",
            _ => throw new AgwException(ErrorCodes.InvalidParam, "The Codex web-search mode is not supported."),
        };
}
