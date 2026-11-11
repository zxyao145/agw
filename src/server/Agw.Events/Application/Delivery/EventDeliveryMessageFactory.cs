using System.Net.Mime;
using System.Text.Json;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Application.Delivery;

public static class EventDeliveryMessageFactory
{
    public static CloudEventEnvelope Create(EventRecord record, Guid? deliveryId)
    {
        var extensions = new Dictionary<string, CloudEventAttributeValue>(StringComparer.Ordinal);
        if (record.Scope is not null)
        {
            extensions.Add("scope", new CloudEventAttributeValue(CloudEventAttributeKind.String, record.Scope));
        }
        if (record.TagsJson is not null)
        {
            var tags =
                JsonSerializer.Deserialize<string[]>(record.TagsJson)
                ?? throw new AgwException(ErrorCodes.InvalidParam);
            extensions.Add(
                "tags",
                new CloudEventAttributeValue(CloudEventAttributeKind.String, string.Join(",", tags))
            );
        }
        if (deliveryId.HasValue)
        {
            extensions.Add(
                "agwdeliveryid",
                new CloudEventAttributeValue(CloudEventAttributeKind.String, deliveryId.Value.ToString("D"))
            );
        }

        return new CloudEventEnvelope
        {
            Id = record.CeId,
            Source = record.CeSource,
            Type = record.Type,
            Time = record.CeTime,
            Subject = record.Subject,
            DataContentType = record.CeDataContentType,
            DataSchema = record.CeDataSchema,
            Data = ReadData(record),
            Extensions = extensions,
        };
    }

    private static CloudEventData? ReadData(EventRecord record)
    {
        if (record.CeData is null)
        {
            return null;
        }
        if (record.CeDataIsBinary)
        {
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Binary,
                Binary = Convert.FromBase64String(record.CeData),
            };
        }
        if (!IsJson(record.CeDataContentType))
        {
            return new CloudEventData { Kind = CloudEventDataKind.Text, Text = record.CeData };
        }

        using var document = JsonDocument.Parse(record.CeData);
        return new CloudEventData
        {
            Kind = CloudEventDataKind.Json,
            Text = record.CeData,
            Json = document.RootElement.Clone(),
        };
    }

    private static bool IsJson(string? contentType)
    {
        if (contentType is null)
        {
            return true;
        }
        var mediaType = new ContentType(contentType).MediaType;
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
