using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Messaging;

/// <summary>Captures the first timestamp for each message within one response stream.</summary>
internal sealed class ResponseMessageTimestamps
{
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<(string? MessageId, string? Author, string Role), DateTimeOffset> _timestamps = [];

    public ResponseMessageTimestamps(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void Stamp(ChatResponseUpdate update)
    {
        var timestamp = GetOrAdd(
            update.MessageId,
            update.AuthorName,
            update.Role,
            update.AdditionalProperties,
            update.CreatedAt
        );
        update.AdditionalProperties = MessageTimestampMetadata.WithCreatedAt(update.AdditionalProperties, timestamp);
        update.CreatedAt = timestamp;
    }

    public void Stamp(AgentResponseUpdate update)
    {
        var timestamp = GetOrAdd(
            update.MessageId,
            update.AuthorName,
            update.Role,
            update.AdditionalProperties,
            update.CreatedAt
        );
        update.AdditionalProperties = MessageTimestampMetadata.WithCreatedAt(update.AdditionalProperties, timestamp);
        update.CreatedAt = timestamp;
    }

    private DateTimeOffset GetOrAdd(
        string? messageId,
        string? author,
        ChatRole? role,
        AdditionalPropertiesDictionary? properties,
        DateTimeOffset? providerTimestamp
    )
    {
        var key = (messageId, author, (role ?? ChatRole.Assistant).Value);
        if (!_timestamps.TryGetValue(key, out var timestamp))
        {
            timestamp =
                MessageTimestampMetadata.GetCreatedAt(properties) ?? providerTimestamp ?? _timeProvider.GetUtcNow();
            _timestamps.Add(key, timestamp);
        }
        return timestamp;
    }
}
