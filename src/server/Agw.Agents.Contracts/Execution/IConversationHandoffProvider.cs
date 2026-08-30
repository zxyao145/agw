using Microsoft.Extensions.AI;

namespace Agw.Agents.Contracts.Execution;

/// <summary>
/// Builds a bounded public-history handoff for an execution target without exposing scoped Tool history.
/// </summary>
public interface IConversationHandoffProvider
{
    Task<ConversationHandoff> CreateAsync(
        Guid conversationId,
        AgentRuntimeType targetType,
        Guid targetId,
        CancellationToken cancellationToken = default
    );
}

public sealed record ConversationHandoff(IReadOnlyList<ChatMessage> Messages, long? ThroughSequence)
{
    public static ConversationHandoff Empty { get; } = new([], null);
}

/// <summary>
/// Internal metadata keys used to keep handoff messages ephemeral and their cursor durable.
/// </summary>
public static class ConversationHandoffMetadata
{
    public const string HandoffMessageKey = "conversationHandoff";
    public const string ThroughSequenceKey = "conversationHandoffThroughSequence";

    public static bool IsHandoffMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.AdditionalProperties?.TryGetValue(HandoffMessageKey, out var value) == true
            && string.Equals(value?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }

    public static void MarkHandoffMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.AdditionalProperties ??= [];
        message.AdditionalProperties[HandoffMessageKey] = true;
    }

    public static void SetThroughSequence(ChatMessage message, long? throughSequence)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!throughSequence.HasValue)
        {
            return;
        }

        message.AdditionalProperties ??= [];
        message.AdditionalProperties[ThroughSequenceKey] = throughSequence.Value;
    }
}
