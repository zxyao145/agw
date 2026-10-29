using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.ExternalAgents.Codex;
using Agw.Agents.ExternalAgents;
using Agw.Providers.Contracts;
using Agw.Shared.Exceptions;
using ClaudeCodeSdk.MAF;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.MAF;
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

        var apiKey = GetApiKey(configuration, EngineKind.ClaudeCode);
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

    internal static CodexAppServerAIAgentOptions ApplyCodex(
        CodexAppServerAIAgentOptions options,
        AgentModelRuntimeConfiguration? configuration
    )
    {
        if (configuration == null)
        {
            return options;
        }

        var apiKey = GetApiKey(configuration, EngineKind.Codex);
        var threadStartParams = options.ThreadStartParams;
        var config = new Dictionary<string, JsonElement>(
            threadStartParams.Config.GetValueOrDefault() ?? new Dictionary<string, JsonElement>(),
            StringComparer.Ordinal
        );
        foreach (
            var key in config
                .Keys.Where(key => key.StartsWith("model_providers.agw.", StringComparison.Ordinal))
                .ToArray()
        )
        {
            config.Remove(key);
        }

        var routedConfig = CodexAgentRuntimeOptionsMapper.MergeProviderConfig(
            config,
            configuration.Provider.Endpoint,
            apiKey
        );
        var selectedThreadStartParams = threadStartParams with
        {
            Model = configuration.Model.Name,
            ModelProvider = "agw",
            Config = new Optional<IReadOnlyDictionary<string, JsonElement>?>(routedConfig),
        };
        var configureThreadResume = options.ConfigureThreadResume;
        return options with
        {
            ThreadStartParams = selectedThreadStartParams,
            ConfigureThreadResume = parameters =>
            {
                var configured = configureThreadResume?.Invoke(parameters) ?? parameters;
                return CodexAgentRuntimeOptionsMapper.ConfigureThreadResume(configured, selectedThreadStartParams);
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

        var apiKey = GetApiKey(configuration, EngineKind.Pi);
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

    private static string GetApiKey(AgentModelRuntimeConfiguration configuration, EngineKind kind)
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
