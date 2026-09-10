using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using ClaudeCodeSdk.MAF;
using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;
using PiAgentSdk.MAF;

namespace Agw.Agents.Execution.Agents.ExternalAgents;

internal static class ExternalAgentModelOptions
{
    internal const string ApiKeyEnvironmentVariable = "AGW_EXTERNAL_MODEL_API_KEY";
    internal const string PiConfigurationEnvironmentVariable = "AGW_PI_MODEL_CONFIGURATION";

    internal static ClaudeCodeAIAgentOptions ApplyClaudeCode(
        ClaudeCodeAIAgentOptions options,
        AgentModelRuntimeConfiguration? configuration
    )
    {
        if (configuration == null)
        {
            return options;
        }

        var apiKey = GetApiKey(configuration, ExternalAgentKind.ClaudeCode);
        var environment = new Dictionary<string, string?>(options.EnvironmentVariables ?? [], StringComparer.Ordinal)
        {
            ["ANTHROPIC_BASE_URL"] = configuration.Provider.Endpoint,
            ["ANTHROPIC_API_KEY"] = apiKey,
            ["ANTHROPIC_AUTH_TOKEN"] = null,
            ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
            ["CLAUDE_CODE_USE_BEDROCK"] = null,
            ["CLAUDE_CODE_USE_VERTEX"] = null,
            ["CLAUDE_CODE_USE_FOUNDRY"] = null,
            ["ANTHROPIC_MODEL"] = configuration.Model.Name,
        };
        return options with
        {
            Model = configuration.Model.Name,
            ExtraArgs = (options.ExtraArgs ?? new Dictionary<string, string?>())
                .Where(argument => argument.Key != "model")
                .ToDictionary(),
            ApiKey = string.Empty,
            BaseUrl = configuration.Provider.Endpoint,
            EnvironmentVariables = environment,
        };
    }

    internal static CodexAIAgentOptions ApplyCodex(
        CodexAIAgentOptions options,
        AgentModelRuntimeConfiguration? configuration
    )
    {
        if (configuration == null)
        {
            return options;
        }

        var apiKey = GetApiKey(configuration, ExternalAgentKind.Codex);
        var original = options.CodexOptions ?? new CodexOptions();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (original.Env != null)
        {
            foreach (var (key, value) in original.Env)
            {
                environment[key] = value;
            }
        }
        else
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    environment[key] = value;
                }
            }
        }
        environment[ApiKeyEnvironmentVariable] = apiKey;

        // Use a dedicated provider so a local model_provider does not redirect this request.
        var config = new Dictionary<string, CodexConfigValue>(
            original.Config ?? new Dictionary<string, CodexConfigValue>()
        );
        foreach (
            var key in config
                .Keys.Where(key => key.StartsWith("model_providers.agw.", StringComparison.Ordinal))
                .ToArray()
        )
        {
            config.Remove(key);
        }
        var providers =
            config.TryGetValue("model_providers", out var configuredProviders)
            && configuredProviders is CodexConfigValue.ObjectValue providerObject
                ? new Dictionary<string, CodexConfigValue>(providerObject.Properties)
                : new Dictionary<string, CodexConfigValue>();
        providers["agw"] = new CodexConfigValue.ObjectValue(
            new Dictionary<string, CodexConfigValue>
            {
                ["name"] = "Agw",
                ["base_url"] = configuration.Provider.Endpoint,
                ["wire_api"] = "responses",
                ["env_key"] = ApiKeyEnvironmentVariable,
                ["requires_openai_auth"] = false,
            }
        );
        config.Remove("model_providers.agw");
        config["model_providers"] = new CodexConfigValue.ObjectValue(providers);
        config["model_provider"] = "agw";
        config["model"] = configuration.Model.Name;
        var thread = options.ThreadOptions ?? new ThreadOptions();
        return options with
        {
            CodexOptions = new CodexOptions
            {
                CodexPathOverride = original.CodexPathOverride,
                BaseUrl = configuration.Provider.Endpoint,
                ApiKey = apiKey,
                Env = environment,
                Config = config,
            },
            ThreadOptions = new ThreadOptions
            {
                Model = configuration.Model.Name,
                SandboxMode = thread.SandboxMode,
                WorkingDirectory = thread.WorkingDirectory,
                SkipGitRepoCheck = thread.SkipGitRepoCheck,
                ModelReasoningEffort = thread.ModelReasoningEffort,
                NetworkAccessEnabled = thread.NetworkAccessEnabled,
                WebSearchMode = thread.WebSearchMode,
                WebSearchEnabled = thread.WebSearchEnabled,
                ApprovalPolicy = thread.ApprovalPolicy,
                AdditionalDirectories = thread.AdditionalDirectories,
            },
        };
    }

    internal static PiAgentAIAgentOptions ApplyPi(
        PiAgentAIAgentOptions options,
        AgentModelRuntimeConfiguration? configuration
    )
    {
        if (configuration == null)
        {
            return options;
        }

        var apiKey = GetApiKey(configuration, ExternalAgentKind.Pi);
        var providerId = $"agw-{configuration.Provider.Id:N}";
        var session = options.SessionOptions;
        var environment = new Dictionary<string, string>(
            session.EnvironmentVariables ?? new Dictionary<string, string>(),
            StringComparer.Ordinal
        )
        {
            [ApiKeyEnvironmentVariable] = apiKey,
            [PiConfigurationEnvironmentVariable] = JsonSerializer.Serialize(
                new
                {
                    providerId,
                    baseUrl = configuration.Provider.Endpoint,
                    api = configuration.Provider.ProviderType switch
                    {
                        ProviderType.OpenAIChatCompletions => "openai-completions",
                        ProviderType.OpenAIResponses => "openai-responses",
                        _ => "anthropic-messages",
                    },
                    model = new
                    {
                        id = configuration.Model.Name,
                        name = configuration.Model.Name,
                        reasoning = false,
                        input = new[] { "text" },
                        cost = new
                        {
                            input = 0,
                            output = 0,
                            cacheRead = 0,
                            cacheWrite = 0,
                        },
                        contextWindow = configuration.Model.MaxContextWindowTokens,
                        maxTokens = configuration.Model.MaxOutputTokens,
                    },
                }
            ),
        };
        return options with
        {
            SessionOptions = session with
            {
                Provider = providerId,
                Model = configuration.Model.Name,
                EnvironmentVariables = environment,
                Extensions =
                [
                    .. session.Extensions ?? [],
                    Path.Combine(AppContext.BaseDirectory, "external-agents", "pi", "agw-model-provider.mjs"),
                ],
            },
        };
    }

    private static string GetApiKey(AgentModelRuntimeConfiguration configuration, ExternalAgentKind kind)
    {
        ExternalAgentDefaults.ValidateProviderType(kind, configuration.Provider.ProviderType);
        if (
            string.IsNullOrWhiteSpace(configuration.Model.Name)
            || string.IsNullOrWhiteSpace(configuration.Provider.Endpoint)
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The selected model provider needs a model name and API endpoint."
            );
        }
        var credentials = configuration
            .Provider.AuthConfigs.Where(auth => auth.Enable && !string.IsNullOrWhiteSpace(auth.ApiKey))
            .ToArray();
        if (credentials.Length == 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The selected model provider has no enabled API key.");
        }
        return credentials[Random.Shared.Next(credentials.Length)].ApiKey!;
    }
}
