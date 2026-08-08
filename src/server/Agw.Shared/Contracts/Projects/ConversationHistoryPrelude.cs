using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Shared.Contracts.Projects;

/// <summary>
/// Carries messages that must be persisted after the current request and before its response.
/// </summary>
public static class ConversationHistoryPrelude
{
    private const string StateKey = "Agw.ConversationHistory.Prelude";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Set(AgentSession? session, IReadOnlyList<ChatMessage> messages)
    {
        if (session == null)
        {
            return;
        }

        if (messages.Count == 0)
        {
            Clear(session);
            return;
        }

        session.StateBag.SetValue(
            StateKey,
            messages
                .Select(message => JsonSerializer.Serialize(message, JsonOptions))
                .ToList());
    }

    public static IReadOnlyList<ChatMessage> Take(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.StateBag.TryGetValue<List<string>>(StateKey, out var payloads) ||
            payloads == null)
        {
            return [];
        }

        session.StateBag.TryRemoveValue(StateKey);
        return payloads
            .Select(payload => JsonSerializer.Deserialize<ChatMessage>(payload, JsonOptions))
            .OfType<ChatMessage>()
            .ToList();
    }

    public static void Clear(AgentSession? session)
    {
        session?.StateBag.TryRemoveValue(StateKey);
    }
}
