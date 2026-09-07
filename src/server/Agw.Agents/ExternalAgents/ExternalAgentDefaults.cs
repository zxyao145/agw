using System.Text.Json;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using OpenAI.CodexSdk.MAF;
using PiAgentSdk.MAF;

namespace Agw.Agents.ExternalAgents;

public static class ExternalAgentDefaults
{
    public static IReadOnlyList<ExternalAgentKind> SupportedKinds { get; } =
    [ExternalAgentKind.ClaudeCode, ExternalAgentKind.Codex, ExternalAgentKind.Pi];

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
