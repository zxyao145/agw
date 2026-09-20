using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;

/// <summary>Separates provider notifications from user-visible conversation content.</summary>
internal static class ClaudeCodeMessagePolicy
{
    internal static bool IsTransportEvent(
        ChatRole? role,
        AdditionalPropertiesDictionary? properties,
        IEnumerable<AIContent> contents
    )
    {
        // The SDK preserves system payloads as text. Classify by protocol metadata, not body text.
        var type = properties?.GetValueOrDefault("type")?.ToString();
        var isSystemEvent =
            type == "system" || (type == null && role == ChatRole.System && properties?.ContainsKey("subtype") == true);
        return isSystemEvent && !contents.Any(content => content is ErrorContent or UsageContent);
    }
}
