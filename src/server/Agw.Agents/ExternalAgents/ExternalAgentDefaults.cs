using System.Text.Json;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using ClaudeCodeSdk.MAF;
using OpenAI.CodexSdk.MAF;
using PiAgentSdk.MAF;
using PermissionMode = ClaudeCodeSdk.Types.PermissionMode;

namespace Agw.Agents.ExternalAgents;

public static class ExternalAgentDefaults
{
    public static IReadOnlyList<ExternalAgentKind> SupportedKinds { get; } =
    [ExternalAgentKind.ClaudeCode, ExternalAgentKind.Codex, ExternalAgentKind.Pi];

    public static IReadOnlyList<ProviderType> GetSupportedProviderTypes(ExternalAgentKind kind) =>
        kind switch
        {
            ExternalAgentKind.ClaudeCode => [ProviderType.Anthropic],
            ExternalAgentKind.Codex => [ProviderType.OpenAIResponses],
            ExternalAgentKind.Pi =>
            [
                ProviderType.OpenAIChatCompletions,
                ProviderType.OpenAIResponses,
                ProviderType.Anthropic,
            ],
            _ => [],
        };

    public static void ValidateProviderType(ExternalAgentKind kind, ProviderType providerType)
    {
        if (!GetSupportedProviderTypes(kind).Contains(providerType))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"{GetDisplayName(kind)} does not support {providerType} model providers."
            );
        }
    }

    public static string GetDisplayName(ExternalAgentKind kind) =>
        kind switch
        {
            ExternalAgentKind.ClaudeCode => "Claude Code",
            ExternalAgentKind.Codex => "OpenAI Codex",
            ExternalAgentKind.Pi => "Pi",
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"External agent kind '{kind}' is not supported."),
        };

    public static string GetDefaultExtra(ExternalAgentKind kind) =>
        kind switch
        {
            ExternalAgentKind.ClaudeCode => JsonUtil.Serialize(
                new ClaudeCodeAIAgentOptions { PermissionMode = PermissionMode.bypassPermissions }
            ),
            ExternalAgentKind.Codex => JsonUtil.Serialize(new CodexAIAgentOptions()),
            ExternalAgentKind.Pi => JsonUtil.Serialize(new PiAgentAIAgentOptions()),
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
