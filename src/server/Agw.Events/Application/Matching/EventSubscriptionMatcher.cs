using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Json.Logic;

namespace Agw.Events.Application.Matching;

public static class EventSubscriptionMatcher
{
    public static IReadOnlyList<EventSubscription> Match(
        EventRecord eventRecord,
        EventSource source,
        IEnumerable<EventSubscription> subscriptions,
        Action<EventSubscription, string>? onFilterFailure = null
    )
    {
        var matches = new List<EventSubscription>();
        foreach (var subscription in subscriptions)
        {
            if (Matches(eventRecord, source, subscription, error => onFilterFailure?.Invoke(subscription, error)))
                matches.Add(subscription);
        }
        return matches;
    }

    private static bool Matches(
        EventRecord eventRecord,
        EventSource source,
        EventSubscription subscription,
        Action<string> onFilterFailure
    )
    {
        if (
            !subscription.Enabled
            || subscription.ManagedState == EventSubscriptionManagedState.Deleted
            || subscription.CreateBy != eventRecord.CreateBy
            || subscription.CreateBy != source.CreateBy
            || eventRecord.SourceId != source.Id
            || !IsTrustedPublisher(eventRecord, source, subscription)
            || !MatchesSourceIds(source, subscription)
            || !MatchesPublisherKeys(source, subscription)
            || !MatchesEventType(eventRecord.Type, subscription.EventTypesJson)
        )
        {
            return false;
        }

        return MatchesFilter(eventRecord, subscription.FilterJson, onFilterFailure);
    }

    private static bool IsTrustedPublisher(EventRecord eventRecord, EventSource source, EventSubscription subscription)
    {
        if (!EventManagedBy.IsExtension(subscription.ManagedBy) || source.Kind != EventSourceKind.Publisher)
        {
            return true;
        }

        // extension 的 publisher key 与 managed_by 的写法相同：ext:<extensionId>。
        // An extension's publisher key has the same form as managed_by: ext:<extensionId>.
        var trustedPublisherKey =
            source.PublisherKey == "agw.execution"
            || source.PublisherKey == "agw.notifications"
            || source.PublisherKey == subscription.ManagedBy
            || source.PublisherKey == $"agw.scheduler:{subscription.ManagedBy}";
        return trustedPublisherKey && eventRecord.CreateBy == source.CreateBy;
    }

    private static bool MatchesSourceIds(EventSource source, EventSubscription subscription)
    {
        var sourceIds = JsonSerializer.Deserialize<Guid[]>(subscription.SourceIdsJson) ?? [];
        return sourceIds.Length == 0 || sourceIds.Contains(source.Id);
    }

    private static bool MatchesPublisherKeys(EventSource source, EventSubscription subscription)
    {
        if (string.IsNullOrEmpty(subscription.PublisherKeysJson))
        {
            return true;
        }

        var publisherKeys = JsonSerializer.Deserialize<string[]>(subscription.PublisherKeysJson) ?? [];
        return publisherKeys.Length == 0
            || (
                source.Kind == EventSourceKind.Publisher
                && publisherKeys.Contains(source.PublisherKey, StringComparer.Ordinal)
            );
    }

    private static bool MatchesEventType(string eventType, string eventTypesJson)
    {
        var patterns = JsonSerializer.Deserialize<string[]>(eventTypesJson) ?? [];
        return patterns.Any(pattern =>
            pattern.EndsWith(".*", StringComparison.Ordinal)
                ? eventType.StartsWith(pattern[..^1], StringComparison.Ordinal)
                : eventType == pattern
        );
    }

    private static bool MatchesFilter(EventRecord eventRecord, string? filterJson, Action<string> onFilterFailure)
    {
        if (string.IsNullOrWhiteSpace(filterJson))
        {
            return true;
        }

        try
        {
            var rule = JsonNode.Parse(filterJson);
            if (!EventSubscriptionFilterValidator.TryValidate(rule, out var error))
            {
                onFilterFailure(error ?? "JsonLogic filter is invalid.");
                return false;
            }

            var booleanRule = new JsonObject { ["!!"] = new JsonArray(rule?.DeepClone()) };
            var result = JsonLogic.Apply(booleanRule, CreateCloudEventContext(eventRecord));
            return result is JsonValue value && value.TryGetValue<bool>(out var matches) && matches;
        }
        catch (Exception exception) when (exception is JsonException or JsonLogicException)
        {
            onFilterFailure(exception.Message);
            return false;
        }
    }

    private static JsonObject CreateCloudEventContext(EventRecord eventRecord) =>
        CloudEventJsonLogicContext.Create(
            new CloudEventDraft
            {
                Id = eventRecord.CeId,
                Source = eventRecord.CeSource,
                Type = eventRecord.Type,
                Time = eventRecord.CeTime,
                Subject = eventRecord.Subject,
                DataContentType = eventRecord.CeDataContentType,
                DataSchema = eventRecord.CeDataSchema,
                Data = ReadData(eventRecord),
                Scope = eventRecord.Scope,
                Tags = JsonSerializer.Deserialize<string[]>(eventRecord.TagsJson ?? "[]"),
            }
        );

    private static CloudEventData? ReadData(EventRecord eventRecord)
    {
        if (eventRecord.CeData is null)
            return null;

        if (eventRecord.CeDataIsBinary)
        {
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Binary,
                Binary = Convert.FromBase64String(eventRecord.CeData),
            };
        }

        if (CloudEventDataCodec.IsExplicitJsonContentType(eventRecord.CeDataContentType))
        {
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = eventRecord.CeData,
                Json = JsonSerializer.Deserialize<JsonElement>(eventRecord.CeData),
            };
        }

        return new CloudEventData { Kind = CloudEventDataKind.Text, Text = eventRecord.CeData };
    }
}
