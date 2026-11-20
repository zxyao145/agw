using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Events.Application.Sources;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Json.Logic;

namespace Agw.Events.Infrastructure.Sources;

internal static class CloudEventDraftMapper
{
    public static CloudEventDraft FromEnvelope(CloudEventEnvelope message, EventWebhookDisplayMappingConfig? display)
    {
        if (message.Extensions.Keys.Any(name => name.StartsWith("agw", StringComparison.Ordinal)))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "CloudEvent extensions cannot use the agw prefix.");
        }

        var tags = message.Extensions.TryGetValue("tags", out var tagValue)
            ? EventTagCodec.Decode(tagValue.Value)
            : null;
        var scope = message.Extensions.TryGetValue("scope", out var scopeValue) ? scopeValue.Value : null;
        var data = message.Data?.Kind switch
        {
            CloudEventDataKind.Json => message.Data.Json,
            CloudEventDataKind.Text => JsonSerializer.SerializeToElement(message.Data.Text),
            _ => (JsonElement?)null,
        };
        var attributes = JsonSerializer.SerializeToNode(
            new
            {
                source = message.Source,
                id = message.Id,
                type = message.Type,
                subject = message.Subject,
                time = message.Time,
                datacontenttype = message.DataContentType,
                dataschema = message.DataSchema,
                scope,
                tags,
                data,
            }
        );
        return new CloudEventDraft
        {
            Id = message.Id,
            Source = message.Source,
            Type = message.Type,
            Time = message.Time,
            Subject = message.Subject,
            DataContentType = message.DataContentType,
            DataSchema = message.DataSchema,
            Data = message.Data,
            Scope = scope,
            Tags = tags,
            Summary = Display(display?.Summary, attributes),
            Link = Display(display?.Link, attributes),
        };
    }

    private static string? Display(JsonElement? rule, JsonNode? attributes)
    {
        if (!rule.HasValue)
            return null;
        var value = JsonLogic.Apply(JsonNode.Parse(rule.Value.GetRawText())!, attributes?.DeepClone());
        return value is null ? null : value.GetValue<string>();
    }
}
