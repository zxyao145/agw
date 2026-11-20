using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Application.Sources;

public static class EventSourceTypeDeclaration
{
    public static IReadOnlyList<string> Resolve(EventSource source, ExtensionGenerationSnapshot? extension) =>
        Read(source, extension).EventTypes;

    internal static (IReadOnlyList<string> EventTypes, string? FixedType) Read(
        EventSource source,
        ExtensionGenerationSnapshot? extension
    ) =>
        source.Kind switch
        {
            EventSourceKind.Webhook => WebhookTypes(source),
            EventSourceKind.HttpPoll => HttpPollTypes(source),
            EventSourceKind.Timer => (
                [
                    EventSourceConfigParser
                        .ParseTimer(JsonSerializer.Deserialize<JsonElement>(source.ConfigJson))
                        .Event.Type,
                ],
                null
            ),
            EventSourceKind.Publisher when source.ManagedBy == EventManagedBy.Agw => (
                EventBuiltInSources.Get(source.Name).EventTypes,
                null
            ),
            EventSourceKind.Publisher
                when extension is not null
                    && source.ManagedBy == EventManagedBy.ForExtension(extension.GenerationId.ExtensionId) => (
                extension.EventPermissions.Publish,
                null
            ),
            EventSourceKind.Provider
                when extension is not null && source.ProviderExtensionId == extension.GenerationId.ExtensionId => (
                extension.FindSourceProvider(source.ProviderId)?.EventTypes
                    ?? throw new AgwException(ErrorCodes.InvalidParam, "The event source provider is unavailable."),
                null
            ),
            _ => throw new AgwException(ErrorCodes.InvalidParam, "Source event types are unavailable."),
        };

    private static (IReadOnlyList<string>, string?) WebhookTypes(EventSource source)
    {
        var config = EventSourceConfigParser.ParseWebhook(JsonSerializer.Deserialize<JsonElement>(source.ConfigJson));
        return (config.EventTypes ?? [config.FixedType!], config.FixedType);
    }

    private static (IReadOnlyList<string>, string?) HttpPollTypes(EventSource source)
    {
        var config = EventSourceConfigParser.Deserialize<EventHttpPollSourceConfig>(
            JsonSerializer.Deserialize<JsonElement>(source.ConfigJson)
        );
        return (config.EventTypes ?? [config.FixedType!], config.FixedType);
    }
}
