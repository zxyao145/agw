using System.Text.Json;
using Agw.Providers.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using ClaudeCodeSdk.MAF;
using OpenAI.CodexSdk.MAF;
using PiAgentSdk.MAF;
using PermissionMode = ClaudeCodeSdk.Types.PermissionMode;

namespace Agw.Agents.ExternalAgents;

public static class ExternalAgentDefaults
{
    public static IReadOnlyList<EngineKind> SupportedKinds { get; } =
    [EngineKind.ClaudeCode, EngineKind.Codex, EngineKind.Pi];

    public static IReadOnlyList<ProviderType> GetSupportedProviderTypes(EngineKind kind) =>
        kind switch
        {
            EngineKind.ClaudeCode => [ProviderType.Anthropic],
            EngineKind.Codex => [ProviderType.OpenAIResponses],
            EngineKind.Pi => [ProviderType.OpenAIChatCompletions, ProviderType.OpenAIResponses, ProviderType.Anthropic],
            _ => [],
        };

    public static void ValidateProviderType(EngineKind kind, ProviderType providerType)
    {
        if (!GetSupportedProviderTypes(kind).Contains(providerType))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"{GetDisplayName(kind)} does not support {providerType} model providers."
            );
        }
    }

    public static string GetDisplayName(EngineKind kind) =>
        kind switch
        {
            EngineKind.ClaudeCode => "Claude Code",
            EngineKind.Codex => "OpenAI Codex",
            EngineKind.Pi => "Pi",
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"External agent kind '{kind}' is not supported."),
        };

    public static string GetDefaultExtra(EngineKind kind) =>
        kind switch
        {
            EngineKind.ClaudeCode => JsonUtil.Serialize(
                new ClaudeCodeAIAgentOptions { PermissionMode = PermissionMode.bypassPermissions }
            ),
            EngineKind.Codex => JsonUtil.Serialize(new CodexAIAgentOptions()),
            EngineKind.Pi => JsonUtil.Serialize(new PiAgentAIAgentOptions()),
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"External agent kind '{kind}' is not supported."),
        };

    public static bool ShouldUseDefaultExtra(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(extra);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && !document.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
