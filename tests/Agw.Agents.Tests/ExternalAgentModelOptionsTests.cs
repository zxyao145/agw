using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Agents.ExternalAgents;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.References;
using Agw.Shared.Exceptions;
using ClaudeCodeSdk.MAF;
using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;
using PiAgentSdk;
using PiAgentSdk.MAF;

namespace Agw.Agents.Tests;

public class ExternalAgentModelOptionsTests
{
    [Theory]
    [InlineData(EngineKind.ClaudeCode, "model")]
    [InlineData(EngineKind.ClaudeCode, "provider")]
    [InlineData(EngineKind.ClaudeCode, "default")]
    [InlineData(EngineKind.Codex, "model")]
    [InlineData(EngineKind.Codex, "provider")]
    [InlineData(EngineKind.Codex, "default")]
    [InlineData(EngineKind.Pi, "model")]
    [InlineData(EngineKind.Pi, "provider")]
    [InlineData(EngineKind.Pi, "default")]
    public void Apply_RebuiltForExistingSession_UpdatesModelAndProviderTogether(EngineKind kind, string change)
    {
        // Arrange
        var sessionId = Guid.CreateVersion7();
        var original = Configuration(kind == EngineKind.Codex ? ProviderType.OpenAIResponses : ProviderType.Anthropic);
        var oldRouting = ReadResumedRouting(kind, sessionId, original);
        AgentModelRuntimeConfiguration? updated =
            change == "default"
                ? null
                : original with
                {
                    Model = original.Model with { Name = "new-model" },
                    Provider =
                        change == "provider"
                            ? original.Provider with
                            {
                                Id = Guid.CreateVersion7(),
                                Endpoint = "https://new.invalid/v1",
                                AuthConfigs = [new ProviderAuthConfigSnapshot(true, "new-key")],
                            }
                            : original.Provider,
                };

        // Act
        var routing = ReadResumedRouting(kind, sessionId, updated);

        // Assert
        if (updated == null)
        {
            Assert.True(string.IsNullOrEmpty(routing.Model));
            Assert.True(string.IsNullOrEmpty(routing.Endpoint));
            Assert.True(string.IsNullOrEmpty(routing.ApiKey));
        }
        else
        {
            Assert.Equal(updated.Model.Name, routing.Model);
            Assert.Equal(updated.Provider.Endpoint, routing.Endpoint);
            Assert.Equal(updated.Provider.AuthConfigs[0].ApiKey, routing.ApiKey);
        }
        Assert.Equal("selected-model", oldRouting.Model);
        Assert.Equal("https://selected.invalid/v1", oldRouting.Endpoint);
        Assert.Equal("selected-key", oldRouting.ApiKey);
    }

    private static (string? Model, string? Endpoint, string? ApiKey) ReadResumedRouting(
        EngineKind kind,
        Guid sessionId,
        AgentModelRuntimeConfiguration? configuration
    )
    {
        switch (kind)
        {
            case EngineKind.ClaudeCode:
                var claude = ExternalAgentModelOptions.ApplyClaudeCode(
                    new ClaudeCodeAIAgentOptions { Resume = sessionId.ToString("N") },
                    configuration
                );
                Assert.Equal(sessionId.ToString("N"), claude.Resume);
                return (
                    claude.Model,
                    claude.BaseUrl,
                    claude.EnvironmentVariables?.GetValueOrDefault("ANTHROPIC_API_KEY")
                );
            case EngineKind.Codex:
                var codex = ExternalAgentModelOptions.ApplyCodex(
                    new CodexAIAgentOptions { ThreadId = sessionId, IsResume = true },
                    configuration
                );
                Assert.Equal(sessionId, codex.ThreadId);
                Assert.True(codex.IsResume);
                return (codex.ThreadOptions?.Model, codex.CodexOptions?.BaseUrl, codex.CodexOptions?.ApiKey);
            case EngineKind.Pi:
                var pi = ExternalAgentModelOptions.ApplyPi(
                    new PiAgentAIAgentOptions { SessionId = sessionId.ToString("D"), IsResume = true },
                    configuration
                );
                Assert.Equal(sessionId.ToString("D"), pi.SessionId);
                Assert.True(pi.IsResume);
                var environment = pi.SessionOptions.EnvironmentVariables;
                var json = environment?.GetValueOrDefault(ExternalAgentModelOptions.PiConfigurationEnvironmentVariable);
                if (json == null)
                    return (pi.SessionOptions.Model, null, null);
                using (var document = JsonDocument.Parse(json))
                {
                    Assert.Equal(
                        pi.SessionOptions.Model,
                        document.RootElement.GetProperty("model").GetProperty("id").GetString()
                    );
                    Assert.Equal(
                        pi.SessionOptions.Provider,
                        document.RootElement.GetProperty("providerId").GetString()
                    );
                    return (
                        pi.SessionOptions.Model,
                        document.RootElement.GetProperty("baseUrl").GetString(),
                        environment![ExternalAgentModelOptions.ApiKeyEnvironmentVariable]
                    );
                }
            default:
                throw new NotSupportedException();
        }
    }

    [Theory]
    [InlineData(EngineKind.ClaudeCode, ProviderType.Anthropic, true)]
    [InlineData(EngineKind.ClaudeCode, ProviderType.OpenAIResponses, false)]
    [InlineData(EngineKind.ClaudeCode, ProviderType.OpenAIChatCompletions, false)]
    [InlineData(EngineKind.Codex, ProviderType.Anthropic, false)]
    [InlineData(EngineKind.Codex, ProviderType.OpenAIResponses, true)]
    [InlineData(EngineKind.Codex, ProviderType.OpenAIChatCompletions, false)]
    [InlineData(EngineKind.Pi, ProviderType.Anthropic, true)]
    [InlineData(EngineKind.Pi, ProviderType.OpenAIResponses, true)]
    [InlineData(EngineKind.Pi, ProviderType.OpenAIChatCompletions, true)]
    public void ValidateProviderType_ProtocolMatrix_MatchesCatalog(EngineKind kind, ProviderType type, bool supported)
    {
        Assert.Equal(supported, ExternalAgentDefaults.GetSupportedProviderTypes(kind).Contains(type));
        if (supported)
        {
            ExternalAgentDefaults.ValidateProviderType(kind, type);
        }
        else
        {
            Assert.Equal(
                ErrorCodes.InvalidParam.Code,
                Assert.Throws<AgwException>(() => ExternalAgentDefaults.ValidateProviderType(kind, type)).Code
            );
        }
    }

    [Fact]
    public void Apply_NoSelection_PreservesOriginalOptions()
    {
        var claude = new ClaudeCodeAIAgentOptions { Model = "extra-model", ApiKey = "extra-key" };
        var codex = new CodexAIAgentOptions { ThreadOptions = new ThreadOptions { Model = "extra-model" } };
        var pi = new PiAgentAIAgentOptions
        {
            SessionOptions = new PiSessionOptions { Model = "extra-model", Provider = "local" },
        };
        Assert.Same(claude, ExternalAgentModelOptions.ApplyClaudeCode(claude, null));
        Assert.Same(codex, ExternalAgentModelOptions.ApplyCodex(codex, null));
        Assert.Same(pi, ExternalAgentModelOptions.ApplyPi(pi, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyClaudeCode_SelectedProvider_OverridesCredentialsAndPreservesSession(bool resume)
    {
        var original = new ClaudeCodeAIAgentOptions
        {
            Model = "extra-model",
            ApiKey = "extra-token",
            BaseUrl = "https://old.invalid",
            Resume = resume ? "session" : null,
            WorkingDirectory = "/workspace",
            MaxTurns = 7,
            ExtraArgs = new Dictionary<string, string?> { ["model"] = "old-argument", ["verbose"] = null },
            EnvironmentVariables = new()
            {
                ["KEEP"] = "value",
                ["ANTHROPIC_API_KEY"] = "old",
                ["ANTHROPIC_AUTH_TOKEN"] = "old-token",
                ["CLAUDE_CODE_USE_VERTEX"] = "1",
            },
        };
        var result = ExternalAgentModelOptions.ApplyClaudeCode(original, Configuration(ProviderType.Anthropic));
        Assert.Equal("selected-model", result.Model);
        Assert.DoesNotContain("model", result.ExtraArgs.Keys);
        Assert.Contains("verbose", result.ExtraArgs.Keys);
        Assert.Equal("https://selected.invalid/v1", result.BaseUrl);
        Assert.Empty(result.ApiKey);
        Assert.Equal("selected-key", result.EnvironmentVariables!["ANTHROPIC_API_KEY"]);
        Assert.Null(result.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Null(result.EnvironmentVariables["CLAUDE_CODE_USE_VERTEX"]);
        Assert.Equal("value", result.EnvironmentVariables["KEEP"]);
        Assert.Equal(original.Resume, result.Resume);
        Assert.Equal(original.WorkingDirectory, result.WorkingDirectory);
        Assert.Equal(7, result.MaxTurns);
        Assert.Equal("old", original.EnvironmentVariables!["ANTHROPIC_API_KEY"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyCodex_SelectedProvider_OverridesRoutingAndPreservesThread(bool resume)
    {
        var original = new CodexAIAgentOptions
        {
            ThreadId = Guid.CreateVersion7(),
            IsResume = resume,
            ThreadOptions = new ThreadOptions
            {
                Model = "extra",
                WorkingDirectory = "/workspace",
                SandboxMode = SandboxMode.WorkspaceWrite,
                ApprovalPolicy = ApprovalMode.Never,
            },
            CodexOptions = new CodexOptions
            {
                ApiKey = "old",
                BaseUrl = "https://old.invalid",
                Env = new Dictionary<string, string>
                {
                    ["KEEP"] = "value",
                    [ExternalAgentModelOptions.ApiKeyEnvironmentVariable] = "old",
                },
                Config = new Dictionary<string, CodexConfigValue>
                {
                    ["model_provider"] = "local",
                    ["model_providers.agw.base_url"] = "https://old.invalid",
                    ["keep"] = true,
                },
            },
        };
        var result = ExternalAgentModelOptions.ApplyCodex(original, Configuration(ProviderType.OpenAIResponses));
        Assert.Equal("selected-model", result.ThreadOptions!.Model);
        Assert.Equal("selected-key", result.CodexOptions!.ApiKey);
        Assert.Equal("https://selected.invalid/v1", result.CodexOptions.BaseUrl);
        Assert.Equal("selected-key", result.CodexOptions.Env![ExternalAgentModelOptions.ApiKeyEnvironmentVariable]);
        Assert.Equal(
            "agw",
            Assert.IsType<CodexConfigValue.StringValue>(result.CodexOptions.Config!["model_provider"]).Value
        );
        var providers = Assert.IsType<CodexConfigValue.ObjectValue>(result.CodexOptions.Config["model_providers"]);
        var provider = Assert.IsType<CodexConfigValue.ObjectValue>(providers.Properties["agw"]);
        Assert.Equal("responses", Assert.IsType<CodexConfigValue.StringValue>(provider.Properties["wire_api"]).Value);
        Assert.Equal(
            "https://selected.invalid/v1",
            Assert.IsType<CodexConfigValue.StringValue>(provider.Properties["base_url"]).Value
        );
        Assert.DoesNotContain("model_providers.agw.base_url", result.CodexOptions.Config.Keys);
        Assert.True(Assert.IsType<CodexConfigValue.BoolValue>(result.CodexOptions.Config["keep"]).Value);
        Assert.Equal("value", result.CodexOptions.Env["KEEP"]);
        Assert.Equal(original.ThreadId, result.ThreadId);
        Assert.Equal(resume, result.IsResume);
        Assert.Equal(SandboxMode.WorkspaceWrite, result.ThreadOptions.SandboxMode);
        Assert.Equal(ApprovalMode.Never, result.ThreadOptions.ApprovalPolicy);
        Assert.Equal("old", original.CodexOptions.ApiKey);
    }

    [Theory]
    [InlineData(ProviderType.Anthropic, "anthropic-messages", false)]
    [InlineData(ProviderType.OpenAIResponses, "openai-responses", true)]
    [InlineData(ProviderType.OpenAIChatCompletions, "openai-completions", true)]
    public void ApplyPi_SelectedProvider_UsesProcessLocalConfiguration(ProviderType type, string api, bool resume)
    {
        var original = new PiAgentAIAgentOptions
        {
            SessionId = "session",
            IsResume = resume,
            SessionOptions = new PiSessionOptions
            {
                Model = "old",
                Provider = "old",
                NoExtensions = true,
                SessionDir = "/sessions",
                WorkingDirectory = "/workspace",
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["PI_CODING_AGENT_DIR"] = "/host-config",
                    ["KEEP"] = "value",
                },
            },
        };
        var result = ExternalAgentModelOptions.ApplyPi(original, Configuration(type));
        Assert.Equal("selected-model", result.SessionOptions.Model);
        Assert.StartsWith("agw-", result.SessionOptions.Provider);
        Assert.Equal(
            "selected-key",
            result.SessionOptions.EnvironmentVariables![ExternalAgentModelOptions.ApiKeyEnvironmentVariable]
        );
        var configuration = result.SessionOptions.EnvironmentVariables[
            ExternalAgentModelOptions.PiConfigurationEnvironmentVariable
        ];
        using var json = JsonDocument.Parse(configuration);
        Assert.Equal(api, json.RootElement.GetProperty("api").GetString());
        Assert.Equal("https://selected.invalid/v1", json.RootElement.GetProperty("baseUrl").GetString());
        Assert.DoesNotContain("selected-key", configuration);
        Assert.Equal("/host-config", result.SessionOptions.EnvironmentVariables["PI_CODING_AGENT_DIR"]);
        Assert.Equal("value", result.SessionOptions.EnvironmentVariables["KEEP"]);
        Assert.True(File.Exists(Assert.Single(result.SessionOptions.Extensions!)));
        Assert.True(result.SessionOptions.NoExtensions);
        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(resume, result.IsResume);
        Assert.Equal("/sessions", result.SessionOptions.SessionDir);
        Assert.Equal("old", original.SessionOptions.Model);
    }

    [Fact]
    public async Task ApplyPi_ConcurrentProviders_DoesNotShareCredentialsOrMutateEnvironment()
    {
        var original = new PiAgentAIAgentOptions();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 10)
                .Select(i =>
                    Task.Run(() =>
                        ExternalAgentModelOptions.ApplyPi(original, Configuration(ProviderType.Anthropic, $"key-{i}"))
                    )
                )
        );
        Assert.Equal(
            10,
            results
                .Select(result =>
                    result.SessionOptions.EnvironmentVariables![ExternalAgentModelOptions.ApiKeyEnvironmentVariable]
                )
                .Distinct()
                .Count()
        );
        Assert.Null(original.SessionOptions.EnvironmentVariables);
    }

    [Fact]
    public void Apply_NoEnabledCredential_FailsWithoutIncludingSecret()
    {
        var configuration = Configuration(ProviderType.Anthropic, "disabled-secret");
        configuration = configuration with
        {
            Provider = configuration.Provider with
            {
                AuthConfigs = [new ProviderAuthConfigSnapshot(false, "disabled-secret")],
            },
        };
        var error = Assert.Throws<AgwException>(() => ExternalAgentModelOptions.ApplyClaudeCode(new(), configuration));
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.DoesNotContain("disabled-secret", error.Message);
    }

    private static AgentModelRuntimeConfiguration Configuration(ProviderType type, string key = "selected-key") =>
        new(
            new ModelProviderModelSnapshot(Guid.CreateVersion7(), "selected-model", 128000, 16000),
            new ModelProviderProviderSnapshot(
                Guid.CreateVersion7(),
                "provider",
                type,
                "https://selected.invalid/v1",
                [new ProviderAuthConfigSnapshot(true, key)]
            )
        );
}
